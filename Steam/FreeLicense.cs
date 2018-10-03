/*
 * Copyright (c) 2013-2018, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Timers;
using Dapper;
using SteamKit2;

namespace SteamDatabaseBackend
{
    class FreeLicense : SteamHandler
    {
        const int REQUEST_RATE_LIMIT = 25; // Steam actually limits at 50, but we're not in a hurry

        private static int AppsRequestedInHour;
        private static Timer FreeLicenseTimer;

        private bool CurrentlyUpdatingNames;
        private readonly Regex PackageRegex;

        public FreeLicense(CallbackManager manager)
            : base(manager)
        {
            PackageRegex = new Regex("RemoveFreeLicense\\( ?(?<subid>[0-9]+), ?'(?<name>.+)' ?\\)", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);

            manager.Subscribe<SteamApps.FreeLicenseCallback>(OnFreeLicenseCallback);

            FreeLicenseTimer = new Timer
            {
                AutoReset = false,
                Interval = TimeSpan.FromMinutes(61).TotalMilliseconds
            };
            FreeLicenseTimer.Elapsed += OnTimer;

            if (LocalConfig.FreeLicensesToRequest.Count > 0)
            {
                AppsRequestedInHour = REQUEST_RATE_LIMIT;
                FreeLicenseTimer.Start();
            }
        }

        private void OnFreeLicenseCallback(SteamApps.FreeLicenseCallback callback)
        {
            JobManager.TryRemoveJob(callback.JobID);

            var packageIDs = callback.GrantedPackages;
            var appIDs = callback.GrantedApps;

            Log.WriteDebug("FreeLicense", "Received free license: {0} ({1} apps: {2}, {3} packages: {4})",
                callback.Result, appIDs.Count, string.Join(", ", appIDs), packageIDs.Count, string.Join(", ", packageIDs));

            if (appIDs.Any())
            {
                JobManager.AddJob(() => Steam.Instance.Apps.PICSGetAccessTokens(appIDs, Enumerable.Empty<uint>()));
            }

            if (packageIDs.Any())
            {
                JobManager.AddJob(() => Steam.Instance.Apps.PICSGetProductInfo(Enumerable.Empty<uint>(), packageIDs));

                TaskManager.RunAsync(() =>
                {
                    RefreshPackageNames();

                    foreach (var subID in packageIDs)
                    {
                        IRC.Instance.SendAnnounce("New free license granted: {0}{1}{2} -{3} {4}",
                            Colors.BLUE, Steam.GetPackageName(subID), Colors.NORMAL,
                            Colors.DARKBLUE, SteamDB.GetPackageURL(subID)
                        );
                    }
                });
            }
        }

        private void RefreshPackageNames()
        {
            if (CurrentlyUpdatingNames)
            {
                return;
            }

            string data;

            try
            {
                CurrentlyUpdatingNames = true;

                var response = WebAuth.PerformRequest("GET", "https://store.steampowered.com/account/licenses/");

                using (var responseStream = response.GetResponseStream())
                {
                    using (var reader = new StreamReader(responseStream))
                    {
                        data = reader.ReadToEnd();
                    }
                }
            }
            catch (WebException e)
            {
                Log.WriteError("FreeLicense", "Failed to fetch account details page: {0}", e.Message);

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

            using (var db = Database.Get())
            {
                // Skip packages that have a store name to avoid messing up history
                var packageData = db.Query<Package>("SELECT `SubID`, `LastKnownName` FROM `Subs` WHERE `SubID` IN @Ids AND `StoreName` = ''", new { Ids = names.Keys });

                foreach (var package in packageData)
                {
                    var newName = names[package.SubID];

                    if (package.LastKnownName != newName)
                    {
                        Log.WriteInfo("FreeLicense", "Changed package name for {0} from \"{1}\" to \"{2}\"", package.SubID, package.LastKnownName, newName);

                        db.Execute("UPDATE `Subs` SET `LastKnownName` = @Name WHERE `SubID` = @SubID", new { package.SubID, Name = newName });

                        db.Execute(
                            SubProcessor.HistoryQuery,
                            new PICSHistory
                            {
                                ID = package.SubID,
                                Key = SteamDB.DATABASE_NAME_TYPE,
                                OldValue = "free on demand; account page",
                                NewValue = newName,
                                Action = "created_info"
                            }
                        );
                    }
                }
            }
        }

        private static void OnTimer(object sender, ElapsedEventArgs e)
        {
            var list = LocalConfig.FreeLicensesToRequest.Take(REQUEST_RATE_LIMIT).ToList();

            foreach (var appid in list)
            {
                LocalConfig.FreeLicensesToRequest.Remove(appid);
            }

            LocalConfig.Save();

            AppsRequestedInHour = list.Count;

            Log.WriteDebug("Free Packages", $"Requesting {AppsRequestedInHour} free apps as the rate limit timer ran");

            JobManager.AddJob(() => Steam.Instance.Apps.RequestFreeLicense(list));

            if (LocalConfig.FreeLicensesToRequest.Count > 0)
            {
                FreeLicenseTimer.Start();
            }
        }

        public static void RequestFromPackage(uint subId, KeyValue kv)
        {
            if ((EBillingType)kv["billingtype"].AsInteger() != EBillingType.FreeOnDemand)
            {
                return;
            }

            if (kv["appids"].Children.Count == 0)
            {
                Log.WriteDebug("Free Packages", $"Package {subId} has no apps");
                return;
            }

            // TODO: Put LicenseList.OwnedApps.ContainsKey() in First() search
            var appid = kv["appids"].Children.First().AsUnsignedInteger();

            if (LicenseList.OwnedApps.ContainsKey(appid))
            {
                return;
            }

            if (kv["status"].AsInteger() != 0) // EPackageStatus.Available
            {
                Log.WriteDebug("Free Packages", $"Package {subId} is not available");
                return;
            }

            if ((ELicenseType)kv["licensetype"].AsInteger() != ELicenseType.SinglePurchase)
            {
                Log.WriteDebug("Free Packages", $"Package {subId} is not single purchase");
                return;
            }

            var dontGrantIfAppIdOwned = kv["extended"]["dontgrantifappidowned"].AsUnsignedInteger();

            if (dontGrantIfAppIdOwned > 0 && LicenseList.OwnedApps.ContainsKey(dontGrantIfAppIdOwned))
            {
                Log.WriteDebug("Free Packages", $"Package {subId} already owns app {dontGrantIfAppIdOwned}");
                return;
            }

            if (kv["extended"]["curatorconnect"].AsInteger() == 1)
            {
                Log.WriteDebug("Free Packages", $"Package {subId} is a curator package");
                return;
            }

            var allowPurchaseFromRestrictedCountries = kv["extended"]["allowpurchasefromrestrictedcountries"].AsBoolean();
            var purchaseRestrictedCountries = kv["extended"]["purchaserestrictedcountries"].AsString();
            
            if (purchaseRestrictedCountries != null && purchaseRestrictedCountries.Contains(AccountInfo.Country) != allowPurchaseFromRestrictedCountries)
            {
                Log.WriteDebug("Free Packages", $"Package {subId} is not available in {AccountInfo.Country}");
                return;
            }

            var startTime = kv["extended"]["starttime"].AsUnsignedLong();
            var expiryTime = kv["extended"]["expirytime"].AsUnsignedLong();
            var now = DateUtils.DateTimeToUnixTime(DateTime.UtcNow);

            if (startTime > now)
            {
                // TODO: Queue until starttime?
                Log.WriteDebug("Free Packages", $"Package {subId} has not reached starttime yet");
                return;
            }

            if (expiryTime > 0 && expiryTime < now)
            {
                Log.WriteDebug("Free Packages", $"Package {subId} has already expired");
                return;
            }

            uint parentAppId;
            bool available;

            using (var db = Database.Get())
            {
                available = db.ExecuteScalar<bool>("SELECT IFNULL(`Value`, \"\") = \"released\" FROM `Apps` LEFT JOIN `AppsInfo` ON `Apps`.`AppID` = `AppsInfo`.`AppID` AND `Key` = (SELECT `ID` FROM `KeyNames` WHERE `Name` = \"common_releasestate\") WHERE `Apps`.`AppID` = @AppID", new { AppID = appid });
                parentAppId = db.ExecuteScalar<uint>("SELECT `Value` FROM `Apps` JOIN `AppsInfo` ON `Apps`.`AppID` = `AppsInfo`.`AppID` WHERE `Key` = (SELECT `ID` FROM `KeyNames` WHERE `Name` = \"common_parent\") AND `Apps`.`AppID` = @AppID AND `AppType` != 3", new { AppID = appid });
            }

            if (!available)
            {
                Log.WriteDebug("Free Packages", $"Package {subId} (app {appid}) did not pass release check");
                return;
            }

            if (parentAppId > 0 && !LicenseList.OwnedApps.ContainsKey(parentAppId))
            {
                Log.WriteDebug("Free Packages", $"Parent app {parentAppId} is not owned to get {appid}");
                return;
            }

            Log.WriteDebug("Free Packages", $"Requesting apps in package {subId}");

            QueueRequest(appid);
        }

        private static void QueueRequest(uint appid)
        {
            if (Settings.IsFullRun || AppsRequestedInHour++ >= REQUEST_RATE_LIMIT)
            {
                if (LocalConfig.FreeLicensesToRequest.Contains(appid))
                {
                    return;
                }

                Log.WriteDebug("Free Packages", $"Adding app {appid} to queue as rate limit is reached");

                LocalConfig.FreeLicensesToRequest.Add(appid);
                LocalConfig.Save();

                return;
            }

            FreeLicenseTimer.Stop();
            FreeLicenseTimer.Start();

            JobManager.AddJob(() => Steam.Instance.Apps.RequestFreeLicense(appid));
        }
    }
}
