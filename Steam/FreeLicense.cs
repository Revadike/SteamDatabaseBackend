/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Timers;
using Dapper;
using Newtonsoft.Json;
using SteamKit2;

namespace SteamDatabaseBackend
{
    internal class FreeLicense : SteamHandler, IDisposable
    {
        private const int REQUEST_RATE_LIMIT = 25; // Steam actually limits at 50, but we're not in a hurry

        public ConcurrentDictionary<uint, uint> FreeLicensesToRequest { get; } = new ConcurrentDictionary<uint, uint>();
        private HashSet<uint> BetasToRequest { get; } = new HashSet<uint>();

        private static int AppsRequestedInHour;
        private static Timer FreeLicenseTimer;

        private bool CurrentlyUpdatingNames;
        private readonly Regex PackageRegex;

        public FreeLicense(CallbackManager manager)
        {
            PackageRegex = new Regex("RemoveFreeLicense\\( ?(?<subid>[0-9]+), ?'(?<name>.+)' ?\\)", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);

            manager.Subscribe<SteamApps.FreeLicenseCallback>(OnFreeLicenseCallback);

            FreeLicenseTimer = new Timer
            {
                AutoReset = false,
                Interval = TimeSpan.FromMinutes(61).TotalMilliseconds
            };
            FreeLicenseTimer.Elapsed += OnTimer;

            var db = Database.Get();
            var data = db.ExecuteScalar<string>("SELECT `Value` FROM `LocalConfig` WHERE `ConfigKey` = 'backend.freelicense.requests'");

            if (data != null)
            {
                FreeLicensesToRequest = JsonConvert.DeserializeObject<ConcurrentDictionary<uint, uint>>(data);
            }

            data = db.ExecuteScalar<string>("SELECT `Value` FROM `LocalConfig` WHERE `ConfigKey` = 'backend.beta.requests'");

            if (data != null)
            {
                BetasToRequest = JsonConvert.DeserializeObject<HashSet<uint>>(data);
            }

            if (FreeLicensesToRequest.IsEmpty && BetasToRequest.Count == 0)
            {
                return;
            }

            Log.WriteInfo(nameof(FreeLicense), $"There are {FreeLicensesToRequest.Count} free licenses and {BetasToRequest.Count} betas to request");

            if (!Settings.IsFullRun)
            {
                AppsRequestedInHour = REQUEST_RATE_LIMIT;
                FreeLicenseTimer.Start();
            }
        }

        public void Dispose()
        {
            if (FreeLicenseTimer != null)
            {
                FreeLicenseTimer.Dispose();
                FreeLicenseTimer = null;
            }
        }

        private void OnFreeLicenseCallback(SteamApps.FreeLicenseCallback callback)
        {
            JobManager.TryRemoveJob(callback.JobID);

            var packageIDs = callback.GrantedPackages;
            var appIDs = callback.GrantedApps;

            Log.WriteDebug(nameof(FreeLicense), $"Received free license: {callback.Result} ({appIDs.Count} apps: {string.Join(", ", appIDs)}, {packageIDs.Count} packages: {string.Join(", ", packageIDs)})");

            if (appIDs.Count > 0)
            {
                JobManager.AddJob(
                    () => Steam.Instance.Apps.PICSGetAccessTokens(appIDs, Enumerable.Empty<uint>()),
                    new PICSTokens.RequestedTokens
                    {
                        Apps = appIDs.ToList()
                    });

                var removed = false;

                foreach (var appid in appIDs)
                {
                    if (FreeLicensesToRequest.TryRemove(appid, out _))
                    {
                        removed = true;
                    }
                }

                if (removed)
                {
                    TaskManager.Run(Save);
                }
            }

            if (packageIDs.Count > 0)
            {
                JobManager.AddJob(
                    () => Steam.Instance.Apps.PICSGetAccessTokens(Enumerable.Empty<uint>(), packageIDs),
                    new PICSTokens.RequestedTokens
                    {
                        Packages = packageIDs.ToList()
                    });

                TaskManager.Run(RefreshPackageNames);
            }
        }

        private async Task RefreshPackageNames()
        {
            if (CurrentlyUpdatingNames)
            {
                return;
            }

            string data;

            try
            {
                CurrentlyUpdatingNames = true;

                var response = await WebAuth.PerformRequest(HttpMethod.Get, new Uri("https://store.steampowered.com/account/licenses/"));
                data = await response.Content.ReadAsStringAsync();
            }
            catch (Exception e)
            {
                Log.WriteError(nameof(FreeLicense), $"Failed to fetch account details page: {e.Message}");

                return;
            }
            finally
            {
                CurrentlyUpdatingNames = false;
            }

            var matches = PackageRegex.Matches(data);
            var names = new Dictionary<uint, string>();

            foreach (Match match in matches)
            {
                var subID = uint.Parse(match.Groups["subid"].Value);
                var name = Encoding.UTF8.GetString(Convert.FromBase64String(match.Groups["name"].Value));

                names[subID] = name;
            }

            if (names.Count == 0)
            {
                Log.WriteError(nameof(FreeLicense), "Failed to find any package names on licenses page");

                return;
            }

            // Skip packages that have a store name to avoid messing up history
            await using var db = await Database.GetConnectionAsync();
            var packageData = await db.QueryAsync<Package>("SELECT `SubID`, `LastKnownName` FROM `Subs` WHERE `SubID` IN @Ids AND `StoreName` = ''", new { Ids = names.Keys });

            foreach (var package in packageData)
            {
                var newName = names[package.SubID];

                if (package.LastKnownName != newName)
                {
                    Log.WriteInfo(nameof(FreeLicense), $"Changed package name for {package.SubID} from \"{package.LastKnownName}\" to \"{newName}\"");

                    await db.ExecuteAsync("UPDATE `Subs` SET `LastKnownName` = @Name WHERE `SubID` = @SubID", new { package.SubID, Name = newName });

                    await db.ExecuteAsync(
                        SubProcessor.HistoryQuery,
                        new PICSHistory
                        {
                            ID = package.SubID,
                            Key = SteamDB.DatabaseNameType,
                            OldValue = "free on demand; account page",
                            NewValue = newName,
                            Action = "created_info"
                        }
                    );
                }
            }
        }

        private void OnTimer(object sender, ElapsedEventArgs e)
        {
            if (!Steam.Instance.IsLoggedOn)
            {
                lock (FreeLicenseTimer)
                {
                    FreeLicenseTimer.Start();
                }

                return;
            }

            if (FreeLicensesToRequest.IsEmpty)
            {
                TaskManager.Run(RequestBetas);
                return;
            }

            var list = FreeLicensesToRequest.Take(REQUEST_RATE_LIMIT).ToList();
            var now = DateUtils.DateTimeToUnixTime(DateTime.UtcNow) - 60;
            Dictionary<uint, ulong> startTimes;

            using (var db = Database.Get())
            {
                startTimes = db.Query(
                    "SELECT `SubID`, `Value` FROM `SubsInfo` WHERE `Key` = @Key AND `SubID` IN @Ids",
                    new
                    {
                        Key = KeyNameCache.GetSubKeyID("extended_starttime"),
                        Ids = list.Select(x => x.Key)
                    }
                ).ToDictionary(x => (uint)x.SubID, x => Convert.ToUInt64((string)x.Value));
            }

            foreach (var (subId, _) in list)
            {
                if (startTimes.TryGetValue(subId, out var startTime) && startTime > now)
                {
                    // If start time has not been reached yet, don't remove this app from the list and keep trying to activate it
                    continue;
                }

                FreeLicensesToRequest.TryRemove(subId, out _);
            }

            TaskManager.Run(Save);
            TaskManager.Run(RequestBetas);

            var appids = list.Select(x => x.Value).Distinct();

            AppsRequestedInHour = appids.Count();

            Log.WriteDebug(nameof(FreeLicense), $"Requesting {AppsRequestedInHour} free apps as the rate limit timer ran: {string.Join(", ", appids)}");

            JobManager.AddJob(() => Steam.Instance.Apps.RequestFreeLicense(appids));

            if (!FreeLicensesToRequest.IsEmpty)
            {
                lock (FreeLicenseTimer)
                {
                    FreeLicenseTimer.Start();
                }
            }
        }

        private async Task RequestBetas()
        {
            var removed = false;

            foreach (var appId in BetasToRequest)
            {
                Log.WriteDebug(nameof(FreeLicense), $"Requesting beta {appId}");

                try
                {
                    var response = await WebAuth.PerformRequest(
                        HttpMethod.Post,
                        new Uri($"https://store.steampowered.com/ajaxrequestplaytestaccess/{appId}"),
                        new List<KeyValuePair<string, string>>
                        {
                            new KeyValuePair<string, string>("sessionid", nameof(SteamDatabaseBackend))
                        }
                    );
                    var data = await response.Content.ReadAsStringAsync();

                    BetasToRequest.Remove(appId);
                    removed = true;

                    Log.WriteDebug(nameof(FreeLicense), $"Beta {appId}: {data}");
                }
                catch (Exception e)
                {
                    Log.WriteWarn(nameof(FreeLicense), $"Failed to request beta {appId}: {e.Message}");
                }
            }

            if (removed)
            {
                await SaveBetas();
            }
        }

        public void AddBeta(uint appId)
        {
            if (BetasToRequest.Contains(appId))
            {
                return;
            }

            BetasToRequest.Add(appId);

            lock (FreeLicenseTimer)
            {
                if (!Settings.IsFullRun && !FreeLicenseTimer.Enabled)
                {
                    FreeLicenseTimer.Start();
                }
            }

            TaskManager.Run(SaveBetas);
        }

        public void RequestFromPackage(uint subId, KeyValue kv)
        {
            if ((EBillingType)kv["billingtype"].AsInteger() != EBillingType.FreeOnDemand)
            {
                return;
            }

            if (kv["appids"].Children.Count == 0)
            {
                Log.WriteDebug(nameof(FreeLicense), $"Package {subId} has no apps");
                return;
            }

            var appId = kv["appids"].Children
                .Select(id => id.AsUnsignedInteger())
                .FirstOrDefault(id => !LicenseList.OwnedApps.ContainsKey(id));

            if (appId == default)
            {
                return;
            }

            if (kv["status"].AsInteger() != 0) // EPackageStatus.Available
            {
                Log.WriteDebug(nameof(FreeLicense), $"Package {subId} is not available");
                return;
            }

            if ((ELicenseType)kv["licensetype"].AsInteger() != ELicenseType.SinglePurchase)
            {
                Log.WriteDebug(nameof(FreeLicense), $"Package {subId} is not single purchase");
                return;
            }

            var dontGrantIfAppIdOwned = kv["extended"]["dontgrantifappidowned"].AsUnsignedInteger();

            if (dontGrantIfAppIdOwned > 0 && LicenseList.OwnedApps.ContainsKey(dontGrantIfAppIdOwned))
            {
                Log.WriteDebug(nameof(FreeLicense), $"Package {subId} already owns app {dontGrantIfAppIdOwned}");
                return;
            }

            if (kv["extended"]["curatorconnect"].AsInteger() == 1)
            {
                Log.WriteDebug(nameof(FreeLicense), $"Package {subId} is a curator package");
                return;
            }

            var allowPurchaseFromRestrictedCountries = kv["extended"]["allowpurchasefromrestrictedcountries"].AsBoolean();
            var purchaseRestrictedCountries = kv["extended"]["purchaserestrictedcountries"].AsString();

            if (purchaseRestrictedCountries != null && purchaseRestrictedCountries.Contains(AccountInfo.Country) != allowPurchaseFromRestrictedCountries)
            {
                Log.WriteDebug(nameof(FreeLicense), $"Package {subId} is not available in {AccountInfo.Country}");
                return;
            }

            var startTime = kv["extended"]["starttime"].AsUnsignedLong();
            var expiryTime = kv["extended"]["expirytime"].AsUnsignedLong();
            var now = DateUtils.DateTimeToUnixTime(DateTime.UtcNow);

            if (expiryTime > 0 && expiryTime < now)
            {
                Log.WriteDebug(nameof(FreeLicense), $"Package {subId} has already expired");
                return;
            }

            if (startTime > 0)
            {
                if (startTime < now)
                {
                    QueueRequest(subId, appId);
                }
                else
                {
                    AddToQueue(subId, appId);
                }

                Log.WriteDebug(nameof(FreeLicense), $"Package {subId} has not reached starttime yet, added to queue");

                return;
            }

            uint parentAppId;
            bool available;

            using (var db = Database.Get())
            {
                available = db.ExecuteScalar<bool>("SELECT IFNULL(`Value`, \"\") = \"released\" FROM `Apps` LEFT JOIN `AppsInfo` ON `Apps`.`AppID` = `AppsInfo`.`AppID` AND `Key` = @Key WHERE `Apps`.`AppID` = @AppID", new { Key = KeyNameCache.GetAppKeyID("common_releasestate"), AppID = appId });
                parentAppId = db.ExecuteScalar<uint>($"SELECT `Value` FROM `Apps` JOIN `AppsInfo` ON `Apps`.`AppID` = `AppsInfo`.`AppID` WHERE `Key` = @Key AND `Apps`.`AppID` = @AppID AND `AppType` NOT IN ({EAppType.Demo:d},{EAppType.MusicAlbum:d})", new { Key = KeyNameCache.GetAppKeyID("common_parent"), AppID = appId });
            }

            if (!available)
            {
                Log.WriteDebug(nameof(FreeLicense), $"Package {subId} (app {appId}) did not pass release check");
                return;
            }

            if (parentAppId > 0 && !LicenseList.OwnedApps.ContainsKey(parentAppId))
            {
                Log.WriteDebug(nameof(FreeLicense), $"Parent app {parentAppId} is not owned to get {appId}");
                return;
            }

            Log.WriteDebug(nameof(FreeLicense), $"Requesting apps in package {subId}");

            QueueRequest(subId, appId);
        }

        private void QueueRequest(uint subId, uint appId)
        {
            if (Settings.IsFullRun || AppsRequestedInHour++ >= REQUEST_RATE_LIMIT)
            {
                Log.WriteDebug(nameof(FreeLicense), $"Adding app {appId} to queue as rate limit is reached");

                AddToQueue(subId, appId);

                return;
            }

            lock (FreeLicenseTimer)
            {
                FreeLicenseTimer.Stop();
                FreeLicenseTimer.Start();
            }

            JobManager.AddJob(() => Steam.Instance.Apps.RequestFreeLicense(appId));
        }

        private void AddToQueue(uint subId, uint appId)
        {
            lock (FreeLicenseTimer)
            {
                if (!Settings.IsFullRun && !FreeLicenseTimer.Enabled)
                {
                    FreeLicenseTimer.Start();
                }
            }

            if (FreeLicensesToRequest.ContainsKey(subId))
            {
                return;
            }

            FreeLicensesToRequest.TryAdd(subId, appId);
            TaskManager.Run(Save);
        }

        private Task Save()
        {
            return LocalConfig.Update("backend.freelicense.requests", JsonConvert.SerializeObject(FreeLicensesToRequest));
        }

        private Task SaveBetas()
        {
            return LocalConfig.Update("backend.beta.requests", JsonConvert.SerializeObject(BetasToRequest));
        }
    }
}
