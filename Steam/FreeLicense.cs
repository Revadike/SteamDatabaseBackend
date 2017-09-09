/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
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
using System.Threading;
using Dapper;
using SteamKit2;

namespace SteamDatabaseBackend
{
    class FreeLicense : SteamHandler
    {
        private static int AppsRequestedInHour;
        private static readonly Queue<uint> AppsToRequest = new Queue<uint>();

        private bool CurrentlyUpdatingNames;
        private readonly Regex PackageRegex;

        public FreeLicense(CallbackManager manager)
            : base(manager)
        {
            PackageRegex = new Regex("RemoveFreeLicense\\( ?(?<subid>[0-9]+), ?'(?<name>.+)' ?\\)", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);

            manager.Subscribe<SteamApps.FreeLicenseCallback>(OnFreeLicenseCallback);

            new Timer(OnTimer, null, TimeSpan.FromMinutes(61), TimeSpan.FromMinutes(61));
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

                names.Add(subID, name);
            }

            using (var db = Database.GetConnection())
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
                            SubProcessor.GetHistoryQuery(),
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

        private static void OnTimer(object state)
        {
            if (AppsToRequest.Count == 0)
            {
                AppsRequestedInHour = 0;
                return;
            }

            var list = AppsToRequest.DequeueChunk(50).ToList();

            AppsRequestedInHour = list.Count;

            Log.WriteDebug("Free Packages", $"Requesting {AppsRequestedInHour} free apps as the rate limit timer ran");

            JobManager.AddJob(() => Steam.Instance.Apps.RequestFreeLicense(list));
        }

        public static void RequestFromPackage(uint subId, KeyValue kv)
        {
            var billingType = (EBillingType)kv["billingtype"].AsInteger();

            if (billingType != EBillingType.FreeOnDemand && billingType != EBillingType.NoCost)
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

            if (expiryTime > now)
            {
                Log.WriteDebug("Free Packages", $"Package {subId} has expired already");
                return;
            }

            if (startTime > 0 && startTime < now)
            {
                // TODO: Queue until starttime?
                Log.WriteDebug("Free Packages", $"Package {subId} has not reached starttime yet");
                return;
            }

            Log.WriteDebug("Free Packages", $"Requesting apps in package {subId}");

            QueueRequest(kv["appids"].Children.First().AsUnsignedInteger());
        }

        private static void QueueRequest(uint appid)
        {
            if (AppsRequestedInHour++ > 50)
            {
                Log.WriteDebug("Free Packages", $"Adding app {appid} to queue as rate limit is reached");

                if (!AppsToRequest.Contains(appid))
                {
                    AppsToRequest.Enqueue(appid);
                }
                
                return;
            }

            JobManager.AddJob(() => Steam.Instance.Apps.RequestFreeLicense(appid));
        }
    }
}
