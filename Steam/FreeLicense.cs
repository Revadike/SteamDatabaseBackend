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
using Dapper;
using SteamKit2;

namespace SteamDatabaseBackend
{
    class FreeLicense : SteamHandler
    {
        private bool CurrentlyUpdatingNames;
        private Regex PackageRegex;

        public FreeLicense(CallbackManager manager)
            : base(manager)
        {
            PackageRegex = new Regex("RemoveFreeLicense\\( ?(?<subid>[0-9]+), ?'(?<name>.+)' ?\\)", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);

            manager.Subscribe<SteamApps.FreeLicenseCallback>(OnFreeLicenseCallback);
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

                TaskManager.Run(() =>
                {
                    RefreshPackageNames();

                    foreach (var subID in packageIDs)
                    {
                        IRC.Instance.SendMain("New free license granted: {0}{1}{2} -{3} {4}",
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

                        db.Execute("UPDATE `Subs` SET `LastKnownName` = @Name WHERE `SubID` = @SubID", new { SubID = package.SubID, Name = newName });

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
    }
}
