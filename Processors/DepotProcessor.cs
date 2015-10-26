/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using SteamKit2;

namespace SteamDatabaseBackend
{
    class DepotProcessor
    {
        public class ManifestJob
        {
            public uint ChangeNumber;
            public uint ParentAppID;
            public uint DepotID;
            public int BuildID;
            public ulong ManifestID;
            public string DepotName;
            public string CDNToken;
            public string Server;
            public byte[] DepotKey;
            public int Tries;
        }

        private readonly CDNClient CDNClient;
        private readonly List<string> CDNServers;
        private readonly ConcurrentDictionary<uint, byte> DepotLocks;

        public DepotProcessor(SteamClient client, CallbackManager manager)
        {
            DepotLocks = new ConcurrentDictionary<uint, byte>();

            CDNClient = new CDNClient(client);

            FileDownloader.SetCDNClient(CDNClient);

            CDNServers = new List<string>
            {
                "cdn.level3.cs.steampowered.com",
                "cdn.akamai.cs.steampowered.com",
                "cdn.highwinds.cs.steampowered.com"
            };

            manager.Subscribe<SteamApps.CDNAuthTokenCallback>(OnCDNAuthTokenCallback);
            manager.Subscribe<SteamApps.DepotKeyCallback>(OnDepotKeyCallback);
        }

        public void Process(uint appID, uint changeNumber, KeyValue depots)
        {
            var requests = new List<ManifestJob>();

            // Get data in format we want first
            foreach (var depot in depots.Children)
            {
                // Ignore these for now, parent app should be updated too anyway
                if (depot["depotfromapp"].Value != null)
                {
                    continue;
                }

                var request = new ManifestJob
                {
                    ChangeNumber = changeNumber,
                    ParentAppID  = appID,
                    DepotName    = depot["name"].AsString()
                };

                // Ignore keys that aren't integers, for example "branches"
                if (!uint.TryParse(depot.Name, out request.DepotID))
                {
                    continue;
                }

                // TODO: instead of locking we could wait for current process to finish
                if (DepotLocks.ContainsKey(request.DepotID))
                {
                    continue;
                }

                // If there is no public manifest for this depot, it still could have some sort of open beta
                if (depot["manifests"]["public"].Value == null || !ulong.TryParse(depot["manifests"]["public"].Value, out request.ManifestID))
                {
                    var branch = depot["manifests"].Children.FirstOrDefault(x => x.Name != "local");

                    if (branch == null || !ulong.TryParse(branch.Value, out request.ManifestID))
                    {
                        using (var db = Database.GetConnection())
                        {
                            db.Execute("INSERT INTO `Depots` (`DepotID`, `Name`) VALUES (@DepotID, @DepotName) ON DUPLICATE KEY UPDATE `Name` = VALUES(`Name`)", new { request.DepotID, request.DepotName });
                        }

                        continue;
                    }

                    request.BuildID = branch["build"].AsInteger();
                }
                else
                {
                    request.BuildID = depots["branches"]["public"]["buildid"].AsInteger();
                }

                requests.Add(request);
            }

            if (!requests.Any())
            {
                return;
            }

            using (var db = Database.GetConnection())
            {
                var dbDepots = db.Query<Depot>("SELECT `DepotID`, `Name`, `BuildID`, `ManifestID`, `LastManifestID` FROM `Depots` WHERE `DepotID` IN @Depots", new { Depots = requests.Select(x => x.DepotID) })
                    .ToDictionary(x => x.DepotID, x => x);

                foreach (var request in requests)
                {
                    Depot dbDepot;

                    if (dbDepots.ContainsKey(request.DepotID))
                    {
                        dbDepot = dbDepots[request.DepotID];

                        if (dbDepot.BuildID > request.BuildID)
                        {
                            // buildid went back in time? this either means a rollback, or a shared depot that isn't synced properly

                            Log.WriteDebug("Depot Processor", "Skipping depot {0} due to old buildid: {1} > {2}", request.DepotID, dbDepot.BuildID, request.BuildID);

                            continue;
                        }

                        if (dbDepot.LastManifestID == request.ManifestID && dbDepot.ManifestID == request.ManifestID && Settings.Current.FullRun < 2)
                        {
                            // Update depot name if changed
                            if (!request.DepotName.Equals(dbDepot.Name))
                            {
                                db.Execute("UPDATE `Depots` SET `Name` = @DepotName WHERE `DepotID` = @DepotID", new { request.DepotID, request.DepotName });
                            }

                            continue;
                        }
                    }
                    else
                    {
                        dbDepot = new Depot();
                    }

                    if (dbDepot.BuildID != request.BuildID || dbDepot.ManifestID != request.ManifestID || !request.DepotName.Equals(dbDepot.Name))
                    {
                        db.Execute(@"INSERT INTO `Depots` (`DepotID`, `Name`, `BuildID`, `ManifestID`) VALUES (@DepotID, @DepotName, @BuildID, @ManifestID)
                                    ON DUPLICATE KEY UPDATE `LastUpdated` = CURRENT_TIMESTAMP(), `Name` = VALUES(`Name`), `BuildID` = VALUES(`BuildID`), `ManifestID` = VALUES(`ManifestID`)",
                        new {
                            request.DepotID,
                            request.DepotName,
                            request.BuildID,
                            request.ManifestID
                        });
                    }

                    if (dbDepot.ManifestID != request.ManifestID)
                    {
                        MakeHistory(db, request, string.Empty, "manifest_change", dbDepot.ManifestID, request.ManifestID);
                    }

                    if (LicenseList.OwnedApps.ContainsKey(request.DepotID) || Settings.Current.FullRun > 1)
                    {
                        DepotLocks.TryAdd(request.DepotID, 1);

                        JobManager.AddJob(() => Steam.Instance.Apps.GetDepotDecryptionKey(request.DepotID, request.ParentAppID), request);
                    }
#if DEBUG
                    else
                    {
                        Log.WriteDebug("Depot Processor", "Skipping depot {0} from app {1} because we don't own it", request.DepotID, request.ParentAppID);
                    }
#endif
                }
            }
        }

        private void OnDepotKeyCallback(SteamApps.DepotKeyCallback callback)
        {
            JobAction job;

            if (!JobManager.TryRemoveJob(callback.JobID, out job))
            {
                RemoveLock(callback.DepotID);

                return;
            }

            var request = job.ManifestJob;

            if (callback.Result != EResult.OK)
            {
                if (callback.Result != EResult.AccessDenied || FileDownloader.IsImportantDepot(request.DepotID))
                {
                    Log.WriteError("Depot Processor", "Failed to get depot key for depot {0} (parent {1}) - {2}", callback.DepotID, request.ParentAppID, callback.Result);
                }

                RemoveLock(request.DepotID);

                return;
            }

            request.DepotKey = callback.DepotKey;
            request.Tries = CDNServers.Count;
            request.Server = GetContentServer();

            JobManager.AddJob(() => Steam.Instance.Apps.GetCDNAuthToken(request.DepotID, request.Server), request);

            var decryptionKey = Utils.ByteArrayToString(callback.DepotKey);

            using (var db = Database.GetConnection())
            {
                var currentDecryptionKey = db.ExecuteScalar<string>("SELECT `Key` FROM `DepotsKeys` WHERE `DepotID` = @DepotID", new { callback.DepotID });

                if (decryptionKey != currentDecryptionKey)
                {
                    if (currentDecryptionKey != null)
                    {
                        Log.WriteInfo("Depot Processor", "Decryption key for {0} changed: {1} -> {2}", callback.DepotID, currentDecryptionKey, decryptionKey);

                        IRC.Instance.SendOps("Decryption key for {0} changed: {1} -> {2}", callback.DepotID, currentDecryptionKey, decryptionKey);
                    }

                    db.Execute("INSERT INTO `DepotsKeys` (`DepotID`, `Key`) VALUES (@DepotID, @Key) ON DUPLICATE KEY UPDATE `Key` = VALUES(`Key`)", new { callback.DepotID, Key = decryptionKey });
                }
            }
        }

        private void OnCDNAuthTokenCallback(SteamApps.CDNAuthTokenCallback callback)
        {
            JobAction job;

            if (!JobManager.TryRemoveJob(callback.JobID, out job))
            {
                return;
            }

            var request = job.ManifestJob;

            if (callback.Result != EResult.OK)
            {
                if (FileDownloader.IsImportantDepot(request.DepotID))
                {
                    Log.WriteError("Depot Processor", "Failed to get CDN auth token for depot {0} (parent {1} - server {2}) - {3} (#{4})",
                        request.DepotID, request.ParentAppID, request.Server, callback.Result, request.Tries);
                }

                if (--request.Tries >= 0)
                {
                    request.Server = GetContentServer(request.Tries);

                    JobManager.AddJob(() => Steam.Instance.Apps.GetCDNAuthToken(request.DepotID, request.Server), request);

                    return;
                }

                RemoveLock(request.DepotID);

                return;
            }

            request.CDNToken = callback.Token;

            // TODO: Using tasks makes every manifest download timeout
            // TODO: which seems to be bug with mono's threadpool implementation
            /*TaskManager.Run(() => DownloadManifest(request)).ContinueWith(task =>
            {
                RemoveLock(request.DepotID);

                Log.WriteDebug("Depot Processor", "Processed depot {0} ({1} depot locks left)", request.DepotID, DepotLocks.Count);
            });*/

            try
            {
                DownloadManifest(request);
            }
            catch (Exception)
            {
                RemoveLock(request.DepotID);
            }
        }

        private void DownloadManifest(ManifestJob request)
        {
            Log.WriteInfo("Depot Processor", "DepotID: {0}", request.DepotID);

            DepotManifest depotManifest = null;
            string lastError = string.Empty;

            // CDN is very random, just keep trying
            for (var i = 0; i <= 5; i++)
            {
                try
                {
                    depotManifest = CDNClient.DownloadManifest(request.DepotID, request.ManifestID, request.Server, request.CDNToken, request.DepotKey);

                    break;
                }
                catch (Exception e)
                {
                    lastError = e.Message;
                }
            }

            if (depotManifest == null)
            {
                Log.WriteError("Depot Processor", "Failed to download depot manifest for depot {0} ({1}: {2}) (#{3})", request.DepotID, request.Server, lastError, request.Tries);

                if (--request.Tries >= 0)
                {
                    request.Server = GetContentServer(request.Tries);

                    JobManager.AddJob(() => Steam.Instance.Apps.GetCDNAuthToken(request.DepotID, request.Server), request);

                    return;
                }

                RemoveLock(request.DepotID); // TODO: Remove this once task in OnCDNAuthTokenCallback is used

                if (FileDownloader.IsImportantDepot(request.DepotID))
                {
                    IRC.Instance.SendOps("{0}[{1}]{2} Failed to download depot {3} manifest ({4}: {5})",
                        Colors.OLIVE, Steam.GetAppName(request.ParentAppID), Colors.NORMAL, request.DepotID, request.Server, lastError);
                }

                return;
            }

            if (FileDownloader.IsImportantDepot(request.DepotID))
            {
                TaskManager.Run(() => FileDownloader.DownloadFilesFromDepot(request, depotManifest));
            }

            // TODO: Task here instead of in OnCDNAuthTokenCallback due to mono's silly threadpool
            TaskManager.Run(() =>
            {
                using(var db = Database.GetConnection())
                {
                    ProcessDepotAfterDownload(db, request, depotManifest);
                }
            }).ContinueWith(task =>
            {
                RemoveLock(request.DepotID);

                Log.WriteDebug("Depot Processor", "Processed depot {0} ({1} depot locks left)", request.DepotID, DepotLocks.Count);
            });
        }

        private static void ProcessDepotAfterDownload(IDbConnection db, ManifestJob request, DepotManifest depotManifest)
        {
            var filesOld = db.Query<DepotFile>("SELECT `ID`, `File`, `Hash`, `Size`, `Flags` FROM `DepotsFiles` WHERE `DepotID` = @DepotID", new { request.DepotID }).ToDictionary(x => x.File, x => x);
            var filesNew = new List<DepotFile>();
            var filesAdded = new List<DepotFile>();
            var shouldHistorize = filesOld.Any(); // Don't historize file additions if we didn't have any data before

            foreach (var file in depotManifest.Files)
            {
                var name = file.FileName.Replace('\\', '/');

                // safe guard
                if (name.Length > 255)
                {
                    ErrorReporter.Notify(new OverflowException(string.Format("File \"{0}\" in depot {1} is too long", name, request.DepotID)));

                    continue;
                }

                var depotFile = new DepotFile
                {
                    DepotID = request.DepotID,
                    File    = name,
                    Size    = file.TotalSize,
                    Flags   = file.Flags
                };

                if (file.FileHash.Length > 0 && !file.Flags.HasFlag(EDepotFileFlag.Directory))
                {
                    depotFile.Hash = Utils.ByteArrayToString(file.FileHash);
                }
                else
                {
                    depotFile.Hash = "0000000000000000000000000000000000000000";
                }

                filesNew.Add(depotFile);
            }

            foreach (var file in filesNew)
            {
                if (filesOld.ContainsKey(file.File))
                {
                    var oldFile = filesOld[file.File];
                    var updateFile = false;

                    if (oldFile.Size != file.Size || !file.Hash.Equals(oldFile.Hash))
                    {
                        MakeHistory(db, request, file.File, "modified", oldFile.Size, file.Size);

                        updateFile = true;
                    }

                    if (oldFile.Flags != file.Flags)
                    {
                        MakeHistory(db, request, file.File, "modified_flags", (ulong)oldFile.Flags, (ulong)file.Flags);

                        updateFile = true;
                    }

                    if (updateFile)
                    {
                        file.ID = oldFile.ID;

                        db.Execute("UPDATE `DepotsFiles` SET `Hash` = @Hash, `Size` = @Size, `Flags` = @Flags WHERE `DepotID` = @DepotID AND `ID` = @ID", file);
                    }

                    filesOld.Remove(file.File);
                }
                else
                {
                    // We want to historize modifications first, and only then deletions and additions
                    filesAdded.Add(file);
                }
            }

            if (filesOld.Any())
            {
                db.Execute("DELETE FROM `DepotsFiles` WHERE `DepotID` = @DepotID AND `ID` IN @Files", new { request.DepotID, Files = filesOld.Select(x => x.Value.ID) });

                db.Execute(GetHistoryQuery(), filesOld.Select(x => new DepotHistory
                {
                    DepotID  = request.DepotID,
                    ChangeID = request.ChangeNumber,
                    Action   = "removed",
                    File     = x.Value.File
                }));
            }

            if (filesAdded.Any())
            {
                db.Execute("INSERT INTO `DepotsFiles` (`DepotID`, `File`, `Hash`, `Size`, `Flags`) VALUES (@DepotID, @File, @Hash, @Size, @Flags)", filesAdded);

                if (shouldHistorize)
                {
                    db.Execute(GetHistoryQuery(), filesAdded.Select(x => new DepotHistory
                    {
                        DepotID  = request.DepotID,
                        ChangeID = request.ChangeNumber,
                        Action   = "added",
                        File     = x.File
                    }));
                }
            }

            db.Execute("UPDATE `Depots` SET `LastManifestID` = @ManifestID WHERE `DepotID` = @DepotID", new { request.DepotID, request.ManifestID });
        }

        public static string GetHistoryQuery()
        {
            return "INSERT INTO `DepotsHistory` (`ChangeID`, `DepotID`, `File`, `Action`, `OldValue`, `NewValue`) VALUES (@ChangeID, @DepotID, @File, @Action, @OldValue, @NewValue)";
        }

        private static void MakeHistory(IDbConnection db, ManifestJob request, string file, string action, ulong oldValue = 0, ulong newValue = 0)
        {
            db.Execute(GetHistoryQuery(),
                new DepotHistory
                {
                    DepotID  = request.DepotID,
                    ChangeID = request.ChangeNumber,
                    Action   = action,
                    File     = file,
                    OldValue = oldValue,
                    NewValue = newValue
                }
            );
        }

        private void RemoveLock(uint depotID)
        {
            byte microsoftWhyIsThereNoRemoveMethodWithoutSecondParam;

            DepotLocks.TryRemove(depotID, out microsoftWhyIsThereNoRemoveMethodWithoutSecondParam);
        }

        private string GetContentServer()
        {
            var i = new Random().Next(CDNServers.Count);

            return CDNServers[i];
        }

        private string GetContentServer(int i)
        {
            i %= CDNServers.Count;

            return CDNServers[i];
        }
    }
}
