/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
        }

        private readonly CDNClient CDNClient;
        private readonly List<string> CDNServers;
        private readonly ConcurrentDictionary<uint, byte> DepotLocks;
        private string UpdateScript;
        private SpinLock UpdateScriptLock;
        private bool SaveLocalConfig;

        public DepotProcessor(SteamClient client)
        {
            UpdateScript = Path.Combine(Application.Path, "files", "update.sh");
            UpdateScriptLock = new SpinLock();
            DepotLocks = new ConcurrentDictionary<uint, byte>();

            CDNClient = new CDNClient(client);

            FileDownloader.SetCDNClient(CDNClient);

            CDNServers = new List<string>
            {
                "cdn.level3.cs.steampowered.com",
                "cdn.akamai.cs.steampowered.com",
                "cdn.highwinds.cs.steampowered.com"
            };
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

            var depotsToDownload = new Dictionary<uint, ManifestJob>();

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

                        depotsToDownload.Add(request.DepotID, request);
                    }
#if DEBUG
                    else
                    {
                        Log.WriteDebug("Depot Processor", "Skipping depot {0} from app {1} because we don't own it", request.DepotID, request.ParentAppID);
                    }
#endif
                }
            }

            if (depotsToDownload.Any())
            {
                TaskManager.Run(async () =>
                {
                    await DownloadDepots(depotsToDownload);
                }, TaskCreationOptions.LongRunning);
            }
        }

        private async Task<byte[]> GetDepotDecryptionKey(uint depotID, uint appID)
        {
            using (var db = Database.GetConnection())
            {
                var currentDecryptionKey = db.ExecuteScalar<string>("SELECT `Key` FROM `DepotsKeys` WHERE `DepotID` = @DepotID", new { depotID });

                if (currentDecryptionKey != null)
                {
                    return Utils.StringToByteArray(currentDecryptionKey);
                }

                var callback = await Steam.Instance.Apps.GetDepotDecryptionKey(depotID, appID);

                if (callback.Result != EResult.OK)
                {
                    if (callback.Result != EResult.AccessDenied)
                    {
                        Log.WriteError("Depot Processor", "No access to depot {0} ({1})", depotID, callback.Result);
                    }

                    return null;
                }

                Log.WriteDebug("Depot Downloader", "Got a new depot key for depot {0}", depotID);

                db.Execute("INSERT INTO `DepotsKeys` (`DepotID`, `Key`) VALUES (@DepotID, @Key) ON DUPLICATE KEY UPDATE `Key` = VALUES(`Key`)", new { depotID, Key = callback.DepotKey });

                return callback.DepotKey;
            }
        }

        private async Task<LocalConfig.CDNAuthToken> GetCDNAuthToken(uint depotID)
        {
            if (LocalConfig.CDNAuthTokens.ContainsKey(depotID))
            {
                var token = LocalConfig.CDNAuthTokens[depotID];

                if (DateTime.Now < token.Expiration)
                {
                    return token;
                }

#if DEBUG
                Log.WriteDebug("Depot Downloader", "Token for depot {0} expired, will request a new one", depotID);
            }
            else
            {
                Log.WriteDebug("Depot Downloader", "Requesting a new token for depot {0}", depotID);
#endif
            }

            var newToken = new LocalConfig.CDNAuthToken
            {
                Server = GetContentServer()
            };

            var tokenCallback = await Steam.Instance.Apps.GetCDNAuthToken(depotID, newToken.Server);

            if (tokenCallback.Result != EResult.OK)
            {
                return null;
            }

            newToken.Token = tokenCallback.Token;
            newToken.Expiration = tokenCallback.Expiration.Subtract(TimeSpan.FromMinutes(1));

            LocalConfig.CDNAuthTokens[depotID] = newToken;

            SaveLocalConfig = true;

            return newToken;
        }

        private async Task DownloadDepots(Dictionary<uint, ManifestJob> depots)
        {
            Log.WriteDebug("Depot Downloader", "Will process {0} depots ({1} depot locks left)", depots.Count, DepotLocks.Count);

            var processTasks = new List<Task<EResult>>();
            bool hasImportantDepots = false;

            foreach (var depot in depots.Values)
            {
                depot.DepotKey = await GetDepotDecryptionKey(depot.DepotID, depot.ParentAppID);

                if (depot.DepotKey == null)
                {
                    RemoveLock(depot.DepotID);

                    continue;
                }

                var cdnToken = await GetCDNAuthToken(depot.DepotID);

                if (depot.DepotKey == null)
                {
                    RemoveLock(depot.DepotID);

                    Log.WriteDebug("Depot Downloader", "Got a depot key for depot {0} but no cdn auth token", depot.DepotID);

                    continue;
                }

                depot.CDNToken = cdnToken.Token;
                depot.Server = cdnToken.Server;

                DepotManifest depotManifest = null;
                string lastError = string.Empty;

                for (var i = 0; i <= 5; i++)
                {
                    try
                    {
                        depotManifest = CDNClient.DownloadManifest(depot.DepotID, depot.ManifestID, depot.Server, depot.CDNToken, depot.DepotKey);

                        break;
                    }
                    catch (Exception e)
                    {
                        Log.WriteWarn("Depot Downloader", "{0} Manifest download failed: {1} - {2}", depot.DepotID, e.GetType(), e.Message);

                        lastError = e.Message;
                    }

                    // TODO: get new auth key if auth fails

                    if (depotManifest == null)
                    {
                        await Task.Delay(Utils.ExponentionalBackoff(i));
                    }
                }

                if (depotManifest == null)
                {
                    RemoveLock(depot.DepotID);

                    Log.WriteError("Depot Processor", "Failed to download depot manifest for depot {0} ({1}: {2})", depot.DepotID, depot.Server, lastError);

                    if (FileDownloader.IsImportantDepot(depot.DepotID))
                    {
                        IRC.Instance.SendOps("{0}[{1}]{2} Failed to download depot {3} manifest ({4}: {5})",
                            Colors.OLIVE, Steam.GetAppName(depot.ParentAppID), Colors.NORMAL, depot.DepotID, depot.Server, lastError);
                    }

                    continue;
                }

                var task = TaskManager.Run(() =>
                {
                    using (var db = Database.GetConnection())
                    {
                        return ProcessDepotAfterDownload(db, depot, depotManifest);
                    }
                });

                processTasks.Add(task);

                if (FileDownloader.IsImportantDepot(depot.DepotID))
                {
                    hasImportantDepots = true;

                    task = TaskManager.Run(() => FileDownloader.DownloadFilesFromDepot(depot, depotManifest), TaskCreationOptions.LongRunning);

                    TaskManager.RegisterErrorHandler(task);

                    processTasks.Add(task);
                }
            }

            if (SaveLocalConfig)
            {
                SaveLocalConfig = false;

                LocalConfig.Save();
            }

            await Task.WhenAll(processTasks);

            // TODO: use ContinueWith on tasks
            if (!hasImportantDepots)
            {
                Log.WriteDebug("Depot Downloader", "Tasks awaited for {0} depot downloads", depots.Count);

                foreach (var depot in depots.Values)
                {
                    RemoveLock(depot.DepotID);
                }

                return;
            }

            var canUpdate = processTasks.All(x => x.Result == EResult.OK || x.Result == EResult.Ignored) && File.Exists(UpdateScript);

#if true
            Log.WriteDebug("Depot Downloader", "Tasks awaited for {0} depot downloads (will run script: {1})", depots.Count, canUpdate);
#endif
            bool lockTaken = false;

            try
            {
                UpdateScriptLock.Enter(ref lockTaken);

                foreach (var depot in depots.Values)
                {
                    // TODO: this only needs to run if any downloaded files changed
                    if (canUpdate && FileDownloader.IsImportantDepot(depot.DepotID))
                    {
                        RunUpdateScript(string.Format("{0} no-git", depot.DepotID));
                    }

                    RemoveLock(depot.DepotID);
                }

                if (canUpdate)
                {
                    RunUpdateScript("0");
                }
            }
            finally
            {
                if (lockTaken)
                {
                    UpdateScriptLock.Exit();
                }
            }
        }

        private void RunUpdateScript(string arg)
        {
            var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = UpdateScript,
                Arguments = arg,
            };
            process.Start();
            process.WaitForExit(120000);
        }

        private EResult ProcessDepotAfterDownload(IDbConnection db, ManifestJob request, DepotManifest depotManifest)
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

            return EResult.OK;
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

            Log.WriteInfo("Depot Downloader", "Processed depot {0} ({1} depot locks left)", depotID, DepotLocks.Count);
        }

        private string GetContentServer()
        {
            var i = Utils.NextRandom(CDNServers.Count);

            return CDNServers[i];
        }
    }
}
