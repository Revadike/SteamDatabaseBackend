/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using SteamKit2;

namespace SteamDatabaseBackend
{
    class FreeLicense : SteamHandler
    {
        public FreeLicense(CallbackManager manager)
            : base(manager)
        {
            manager.Subscribe<SteamApps.FreeLicenseCallback>(OnFreeLicenseCallback);
        }

        private static void OnFreeLicenseCallback(SteamApps.FreeLicenseCallback callback)
        {
            JobManager.TryRemoveJob(callback.JobID);

            var packageIDs = callback.GrantedPackages;
            var appIDs = callback.GrantedApps;

            Log.WriteDebug("FreeLicense", "Received free license: {0} ({1} apps: {2}, {3} packages: {4})",
                callback.Result, appIDs.Count, string.Join(", ", appIDs), packageIDs.Count, string.Join(", ", packageIDs));

            if (appIDs.Count > 0)
            {
                JobManager.AddJob(() => Steam.Instance.Apps.PICSGetAccessTokens(appIDs, Enumerable.Empty<uint>()));
            }

            if (packageIDs.Count > 0)
            {
                JobManager.AddJob(() => Steam.Instance.Apps.PICSGetProductInfo(Enumerable.Empty<uint>(), packageIDs));

                // We don't want to block our main thread with web requests
                TaskManager.Run(() =>
                {
                    string data = null;

                    try
                    {
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
                    }

                    using (var db = Database.GetConnection())
                    {
                        foreach (var package in packageIDs)
                        {
                            var packageData = db.Query<Package>("SELECT `SubID`, `Name`, `LastKnownName` FROM `Subs` WHERE `SubID` = @SubID", new { SubID = package }).FirstOrDefault();

                            if (!string.IsNullOrEmpty(data))
                            {
                                // Tell me all about using regex
                                var match = Regex.Match(data, string.Format("RemoveFreeLicense\\( ?{0}, ?'(.+)' ?\\)", package));

                                if (match.Success)
                                {
                                    var grantedName = Encoding.UTF8.GetString(Convert.FromBase64String(match.Groups[1].Value));

                                    // Update last known name if we can
                                    if (packageData.SubID > 0 && (string.IsNullOrEmpty(packageData.LastKnownName) || packageData.LastKnownName.StartsWith("Steam Sub ", StringComparison.Ordinal)))
                                    {
                                        db.Execute("UPDATE `Subs` SET `LastKnownName` = @Name WHERE `SubID` = @SubID", new { SubID = package, Name = grantedName });

                                        db.Execute(SubProcessor.GetHistoryQuery(),
                                            new PICSHistory
                                            {
                                                ID       = package,
                                                Key      = SteamDB.DATABASE_NAME_TYPE,
                                                OldValue = "free on demand; account page",
                                                NewValue = grantedName,
                                                Action   = "created_info"
                                            }
                                        );

                                        // Add a app comment on each app in this package
                                        var comment = string.Format("This app is in a free on demand package called <b>{0}</b>", SecurityElement.Escape(grantedName));
                                        var apps = db.Query<PackageApp>("SELECT `AppID` FROM `SubsApps` WHERE `SubID` = @SubID", new { SubID = package }).ToList();
                                        var types = db.Query<App>("SELECT `AppID` FROM `Apps` WHERE `AppType` > 0 AND `AppID` IN @Ids", new { Ids = apps.Select(x => x.AppID) }).ToDictionary(x => x.AppID, x => true);
                                        var key = db.ExecuteScalar<uint>("SELECT `ID` FROM `KeyNames` WHERE `Name` = 'website_comment'");

                                        foreach (var app in apps)
                                        {
                                            if (types.ContainsKey(app.AppID))
                                            {
                                                continue;
                                            }

                                            db.Execute("INSERT INTO `AppsInfo` VALUES (@AppID, @Key, @Value) ON DUPLICATE KEY UPDATE `Key` = `Key`", new {app.AppID, Key = key, value = comment });
                                        }
                                    }

                                    packageData.LastKnownName = grantedName;
                                }
                            }

                            IRC.Instance.SendMain("New free license granted: {0}{1}{2} -{3} {4}",
                                Colors.BLUE, Steam.FormatPackageName(package, packageData), Colors.NORMAL,
                                Colors.DARKBLUE, SteamDB.GetPackageURL(package)
                            );
                        }
                    }
                });
            }
        }
    }
}
