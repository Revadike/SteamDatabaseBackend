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
using SteamKit2.Internal;

namespace SteamDatabaseBackend
{
    class FreeLicense : ClientMsgHandler
    {
        public JobID RequestFreeLicence(IEnumerable<uint> appIDs)
        {
            var msg = new ClientMsgProtobuf<CMsgClientRequestFreeLicense>(EMsg.ClientRequestFreeLicense);
            msg.Body.appids.AddRange(appIDs);

            var jid = Client.GetNextJobID();
            msg.SourceJobID = jid;

            Client.Send(msg);

            return jid;
        }

        public override void HandleMsg(IPacketMsg packetMsg)
        {
            if (packetMsg.MsgType == EMsg.ClientRequestFreeLicenseResponse)
            {
                HandleClientRequestFreeLicenseResponse(packetMsg);
            }
        }

        private static void HandleClientRequestFreeLicenseResponse(IPacketMsg packetMsg)
        {
            var resp = new ClientMsgProtobuf<CMsgClientRequestFreeLicenseResponse>(packetMsg);

            JobAction job;
            JobManager.TryRemoveJob(packetMsg.TargetJobID, out job);

            var packageIDs = resp.Body.granted_packageids;
            var appIDs = resp.Body.granted_appids;

            Log.WriteDebug("FreeLicense", "Received free license: {0} ({1} apps, {2} packages)", (EResult)resp.Body.eresult, appIDs.Count, packageIDs.Count);

            if (appIDs.Count > 0)
            {
                JobManager.AddJob(() => Steam.Instance.Apps.PICSGetAccessTokens(appIDs, Enumerable.Empty<uint>()));
            }

            if (packageIDs.Count > 0)
            {
                JobManager.AddJob(() => Steam.Instance.Apps.PICSGetProductInfo(Enumerable.Empty<uint>(), packageIDs));

                // TODO: We could re-queue apps in these packages as well

                // We don't want to block our main thread with web requests
                TaskManager.Run(() =>
                {
                    string data = null;

                    try
                    {
                        var response = WebAuth.PerformRequest("GET", "https://store.steampowered.com/account/");

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

                    foreach (var package in packageIDs)
                    {
                        Package packageData;

                        using (var db = Database.GetConnection())
                        {
                            packageData = db.Query<Package>("SELECT `SubID`, `Name`, `LastKnownName` FROM `Subs` WHERE `SubID` = @SubID", new { SubID = package }).FirstOrDefault();
                        }

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
                                    using (var db = Database.GetConnection())
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
                                        var comment = string.Format("This app is in a free on demand package called <b>{0}</b>", System.Security.SecurityElement.Escape(grantedName));
                                        var apps = db.Query<PackageApp>("SELECT `AppID` FROM `SubsApps` WHERE `SubID` = @SubID", new { SubID = package }).ToList();
                                        var types = db.Query<App>("SELECT `AppID` FROM `Apps` WHERE `AppType` = 0 AND `AppID` IN @Ids", new { Ids = apps.Select(x => x.AppID) }).ToDictionary(x => x.AppID, x => true);
                                        var key = db.ExecuteScalar<uint>("SELECT `ID` FROM `KeyNames` WHERE `Name` = 'website_comment'");

                                        foreach (var app in apps)
                                        {
                                            if (types.ContainsKey(app.AppID))
                                            {
                                                continue;
                                            }

                                            db.Execute("INSERT INTO `AppsInfo` VALUES (@AppID, @Key, @Value) ON DUPLICATE KEY UPDATE `Key` = `Key`", new { AppID = app.AppID, Key = key, value = comment });
                                        }
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
                });
            }
        }
    }
}
