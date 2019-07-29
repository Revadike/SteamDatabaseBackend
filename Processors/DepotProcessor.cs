/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Dapper;
using SteamKit2;

namespace SteamDatabaseBackend
{
    internal class DepotProcessor : IDisposable
    {
        public class ManifestJob
        {
            public uint ChangeNumber;
            public uint DepotID;
            public int BuildID;
            public ulong ManifestID;
            public string DepotName;
            public CDNClient.Server Server;
            public byte[] DepotKey;
            public EResult Result = EResult.Fail;
        }

        public const string HistoryQuery = "INSERT INTO `DepotsHistory` (`ChangeID`, `DepotID`, `File`, `Action`, `OldValue`, `NewValue`) VALUES (@ChangeID, @DepotID, @File, @Action, @OldValue, @NewValue)";

        private static readonly object UpdateScriptLock = new object();

        private CDNClient CDNClient;
        private readonly Dictionary<uint, byte> DepotLocks;
        private List<CDNClient.Server> CDNServers;
        private readonly string UpdateScript;
        private bool SaveLocalConfig;

        public int DepotLocksCount => DepotLocks.Count;

        public DepotProcessor(SteamClient client, CallbackManager manager)
        {
            UpdateScript = Path.Combine(Application.Path, "files", "update.sh");
            DepotLocks = new Dictionary<uint, byte>();
            CDNClient = new CDNClient(client);
            CDNServers = new List<CDNClient.Server>
            {
                new DnsEndPoint("valve500.steamcontent.com", 80)
            };

            CDNClient.RequestTimeout = TimeSpan.FromSeconds(30);

            FileDownloader.SetCDNClient(CDNClient);

            manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);

            TaskManager.RunAsync(UpdateContentServerList);
        }

        public void Dispose()
        {
            if (CDNClient != null)
            {
                CDNClient.Dispose();
                CDNClient = null;
            }
        }

        private void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            TaskManager.RunAsync(UpdateContentServerList);
        }

        private async void UpdateContentServerList()
        {
            KeyValue response;

            using (var steamDirectory = Steam.Configuration.GetAsyncWebAPIInterface("ISteamDirectory"))
            {
                steamDirectory.Timeout = TimeSpan.FromSeconds(30);

                try
                {
                    response = await steamDirectory.CallAsync(HttpMethod.Get, "GetCSList", 1,
                        new Dictionary<string, object>
                        {
                            { "cellid", LocalConfig.Current.CellID },
                            { "maxcount", "20" } // Use the first 20 servers as they're sorted by cellid and we want low latency
                        });

                    if ((EResult)response["result"].AsInteger() != EResult.OK)
                    {
                        throw new Exception($"GetCSList result is EResult.${response["result"]}");
                    }
                }
                catch (Exception e)
                {
                    ErrorReporter.Notify("Depot Downloader", e);

                    return;
                }
            }

            // Note: There are servers like `origin5-sea1.steamcontent.com`
            // which are hosted in Seattle, and most likely are the source of truth for
            // game content, perhaps we should filter to these. But the latency is poor from Europe.

            var newServers = response["serverlist"].Children.Select(x => (CDNClient.Server)new DnsEndPoint(x.Value.ToString(), 80)).ToList();

            if (newServers.Count > 0)
            {
                CDNServers = newServers;
            }
        }

        public async Task Process(uint appID, uint changeNumber, KeyValue depots)
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
                    DepotName = depot["name"].AsString()
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

                // SteamVR trickery
                if (appID == 250820
                    && depot["manifests"]["beta"].Value != null
                    && depots["branches"]["beta"]["buildid"].AsInteger() > depots["branches"]["public"]["buildid"].AsInteger())
                {
                    request.BuildID = depots["branches"]["beta"]["buildid"].AsInteger();
                    request.ManifestID = ulong.Parse(depot["manifests"]["beta"].Value);
                }
                else
                if (depot["manifests"]["public"].Value == null || !ulong.TryParse(depot["manifests"]["public"].Value, out request.ManifestID))
                {
                    var branch = depot["manifests"].Children.Find(x => x.Name != "local");

                    if (branch == null || !ulong.TryParse(branch.Value, out request.ManifestID))
                    {
                        using (var db = await Database.GetConnectionAsync())
                        {
                            await db.ExecuteAsync("INSERT INTO `Depots` (`DepotID`, `Name`) VALUES (@DepotID, @DepotName) ON DUPLICATE KEY UPDATE `DepotID` = VALUES(`DepotID`)", new { request.DepotID, request.DepotName });
                        }

                        continue;
                    }

                    Log.WriteDebug("Depot Downloader", "Depot {0} (from {1}) has no public branch, but there is another one", request.DepotID, appID);

                    request.BuildID = depots["branches"][branch.Name]["buildid"].AsInteger();
                }
                else
                {
                    request.BuildID = depots["branches"]["public"]["buildid"].AsInteger();
                }

                requests.Add(request);
            }

            if (requests.Count == 0)
            {
                return;
            }

            var depotsToDownload = new List<ManifestJob>();

            using (var db = await Database.GetConnectionAsync())
            {
                await db.ExecuteAsync("INSERT INTO `Builds` (`BuildID`, `ChangeID`, `AppID`) VALUES (@BuildID, @ChangeNumber, @AppID) ON DUPLICATE KEY UPDATE `AppID` = VALUES(`AppID`)",
                new
                {
                    requests[0].BuildID,
                    requests[0].ChangeNumber,
                    appID
                });

                var depotIds = requests.Select(x => x.DepotID).ToList();
                var dbDepots = (await db.QueryAsync<Depot>("SELECT `DepotID`, `Name`, `BuildID`, `ManifestID`, `LastManifestID` FROM `Depots` WHERE `DepotID` IN @depotIds", new { depotIds }))
                    .ToDictionary(x => x.DepotID, x => x);

                var decryptionKeys = (await db.QueryAsync<DepotKey>("SELECT `DepotID`, `Key` FROM `DepotsKeys` WHERE `DepotID` IN @depotIds", new { depotIds }))
                    .ToDictionary(x => x.DepotID, x => Utils.StringToByteArray(x.Key));

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

                        if (dbDepot.LastManifestID == request.ManifestID && dbDepot.ManifestID == request.ManifestID && Settings.Current.FullRun != FullRunState.WithForcedDepots)
                        {
                            // Update depot name if changed
                            if (!request.DepotName.Equals(dbDepot.Name))
                            {
                                await db.ExecuteAsync("UPDATE `Depots` SET `Name` = @DepotName WHERE `DepotID` = @DepotID", new { request.DepotID, request.DepotName });
                            }

                            continue;
                        }
                    }
                    else
                    {
                        dbDepot = new Depot();
                    }

                    decryptionKeys.TryGetValue(request.DepotID, out request.DepotKey);

                    if (dbDepot.BuildID != request.BuildID || dbDepot.ManifestID != request.ManifestID || !request.DepotName.Equals(dbDepot.Name))
                    {
                        await db.ExecuteAsync(@"INSERT INTO `Depots` (`DepotID`, `Name`, `BuildID`, `ManifestID`) VALUES (@DepotID, @DepotName, @BuildID, @ManifestID)
                                    ON DUPLICATE KEY UPDATE `LastUpdated` = CURRENT_TIMESTAMP(), `Name` = VALUES(`Name`), `BuildID` = VALUES(`BuildID`), `ManifestID` = VALUES(`ManifestID`)",
                        new
                        {
                            request.DepotID,
                            request.DepotName,
                            request.BuildID,
                            request.ManifestID
                        });
                    }

                    if (dbDepot.ManifestID != request.ManifestID)
                    {
                        await MakeHistory(db, null, request, string.Empty, "manifest_change", dbDepot.ManifestID, request.ManifestID);
                    }

                    lock (DepotLocks)
                    {
                        // This doesn't really save us from concurrency issues
                        if (DepotLocks.ContainsKey(request.DepotID))
                        {
                            Log.WriteWarn("Depot Processor", "Depot {0} was locked in another thread", request.DepotID);
                            continue;
                        }

                        DepotLocks.Add(request.DepotID, 1);
                    }

                    depotsToDownload.Add(request);
                }
            }

            if (depotsToDownload.Count > 0)
            {
                _ = TaskManager.Run(async () =>
                {
                    try
                    {
                        await DownloadDepots(appID, depotsToDownload);
                    }
                    catch (Exception e)
                    {
                        ErrorReporter.Notify("Depot Processor", e);
                    }

                    foreach (var depot in depotsToDownload)
                    {
                        RemoveLock(depot.DepotID);
                    }
                });
            }
        }

        private async Task GetDepotDecryptionKey(SteamApps instance, ManifestJob depot, uint appID)
        {
            if (!LicenseList.OwnedApps.ContainsKey(depot.DepotID))
            {
                return;
            }

            var task = instance.GetDepotDecryptionKey(depot.DepotID, appID);
            task.Timeout = TimeSpan.FromMinutes(15);

            SteamApps.DepotKeyCallback callback;

            try
            {
                callback = await task;
            }
            catch (TaskCanceledException)
            {
                Log.WriteWarn("Depot Processor", $"Decryption key timed out for {depot.DepotID}");

                return;
            }

            if (callback.Result != EResult.OK)
            {
                if (callback.Result != EResult.AccessDenied)
                {
                    Log.WriteWarn("Depot Processor", $"No access to depot {depot.DepotID} ({callback.Result})");
                }

                return;
            }

            Log.WriteDebug("Depot Downloader", $"Got a new depot key for depot {depot.DepotID}");

            using (var db = await Database.GetConnectionAsync())
            {
                await db.ExecuteAsync("INSERT INTO `DepotsKeys` (`DepotID`, `Key`) VALUES (@DepotID, @Key) ON DUPLICATE KEY UPDATE `Key` = VALUES(`Key`)", new { depot.DepotID, Key = Utils.ByteArrayToString(callback.DepotKey) });
            }

            depot.DepotKey = callback.DepotKey;
        }

        private async Task DownloadDepots(uint appID, List<ManifestJob> depots)
        {
            Log.WriteDebug("Depot Downloader", "Will process {0} depots ({1} depot locks left)", depots.Count, DepotLocks.Count);

            var processTasks = new List<Task<EResult>>();
            var anyFilesDownloaded = false;

            foreach (var depot in depots)
            {
                if (depot.DepotKey == null)
                {
                    await GetDepotDecryptionKey(Steam.Instance.Apps, depot, appID);

                    if (depot.DepotKey == null)
                    {
                        RemoveLock(depot.DepotID);

                        continue;
                    }
                }

                depot.Server = GetContentServer();

                DepotManifest depotManifest = null;
                var lastError = string.Empty;

                for (var i = 0; i <= 5; i++)
                {
                    try
                    {
                        depotManifest = await CDNClient.DownloadManifestAsync(depot.DepotID, depot.ManifestID, depot.Server, string.Empty, depot.DepotKey);

                        break;
                    }
                    catch (Exception e)
                    {
                        lastError = e.Message;

                        Log.WriteError("Depot Processor", "Failed to download depot manifest for app {0} depot {1} ({2}: {3}) (#{4})", appID, depot.DepotID, depot.Server, lastError, i);
                    }

                    depot.Server = GetContentServer();

                    if (depotManifest == null)
                    {
                        await Task.Delay(Utils.ExponentionalBackoff(i));
                    }
                }

                if (depotManifest == null)
                {
                    RemoveLock(depot.DepotID);

                    if (FileDownloader.IsImportantDepot(depot.DepotID))
                    {
                        IRC.Instance.SendOps("{0}[{1}]{2} Failed to download manifest ({3})",
                            Colors.OLIVE, depot.DepotName, Colors.NORMAL, lastError);
                    }

                    if (!Settings.IsFullRun)
                    {
                        JobManager.AddJob(() => Steam.Instance.Apps.PICSGetAccessTokens(appID, null));
                    }

                    continue;
                }

                var task = ProcessDepotAfterDownload(depot, depotManifest);

                processTasks.Add(task);

                if (!FileDownloader.IsImportantDepot(depot.DepotID))
                {
                    continue;
                }

                task = TaskManager.Run(async () =>
                {
                    var result = EResult.Fail;

                    try
                    {
                        result = await FileDownloader.DownloadFilesFromDepot(depot, depotManifest);

                        if (result == EResult.OK)
                        {
                            anyFilesDownloaded = true;
                        }
                    }
                    catch (Exception e)
                    {
                        ErrorReporter.Notify($"Depot Processor {depot.DepotID}", e);
                    }

                    return result;
                }).Unwrap();

                processTasks.Add(task);
            }

            if (SaveLocalConfig)
            {
                SaveLocalConfig = false;

                LocalConfig.Save();
            }

            await Task.WhenAll(processTasks).ConfigureAwait(false);

            Log.WriteDebug("Depot Downloader", "{0} depot downloads finished", depots.Count);

            // TODO: use ContinueWith on tasks
            if (!anyFilesDownloaded)
            {
                foreach (var depot in depots)
                {
                    RemoveLock(depot.DepotID);
                }

                return;
            }

            if (!File.Exists(UpdateScript))
            {
                return;
            }

            lock (UpdateScriptLock)
            {
                foreach (var depot in depots)
                {
                    if (depot.Result == EResult.OK)
                    {
                        RunUpdateScript(UpdateScript, string.Format("{0} no-git", depot.DepotID));
                    }
                    else if (depot.Result != EResult.Ignored)
                    {
                        Log.WriteWarn("Depot Processor", $"Download failed for {depot.DepotID}");

                        using (var db = Database.Get())
                        {
                            // Mark this depot for redownload
                            db.Execute("UPDATE `Depots` SET `LastManifestID` = 0 WHERE `DepotID` = @DepotID", new { depot.DepotID });
                        }
                    }

                    RemoveLock(depot.DepotID);
                }

                // Only commit changes if all depots downloaded
                if (processTasks.All(x => x.Result == EResult.OK || x.Result == EResult.Ignored))
                {
                    if (!RunUpdateScript(appID, depots[0].BuildID))
                    {
                        RunUpdateScript(UpdateScript, "0");
                    }
                }
                else
                {
                    Log.WriteDebug("Depot Processor", "Reprocessing the app {0} because some files failed to download", appID);

                    IRC.Instance.SendOps("{0}[{1}]{2} Reprocessing the app due to download failures",
                        Colors.OLIVE, Steam.GetAppName(appID), Colors.NORMAL
                    );

                    JobManager.AddJob(() => Steam.Instance.Apps.PICSGetAccessTokens(appID, null));
                }
            }
        }

        private void RunUpdateScript(string script, string arg)
        {
            Log.WriteDebug("Depot Downloader", $"Running update script: {script} {arg}");

            using (var process = new System.Diagnostics.Process())
            {
                process.StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = script,
                    Arguments = arg,
                };
                process.Start();
                process.WaitForExit(120000);
            }
        }

        private bool RunUpdateScript(uint appID, int buildID)
        {
            var downloadFolder = FileDownloader.GetAppDownloadFolder(appID);

            if (downloadFolder == null)
            {
                return false;
            }

            var updateScript = Path.Combine(Application.Path, "files", downloadFolder, "update.sh");

            if (!File.Exists(updateScript))
            {
                return false;
            }

            RunUpdateScript(updateScript, buildID.ToString());

            return true;
        }

        private async Task<EResult> ProcessDepotAfterDownload(ManifestJob request, DepotManifest depotManifest)
        {
            using (var db = await Database.GetConnectionAsync())
            using (var transaction = await db.BeginTransactionAsync())
            {
                var result = await ProcessDepotAfterDownload(db, transaction, request, depotManifest);
                await transaction.CommitAsync();
                return result;
            }
        }

        private async Task<EResult> ProcessDepotAfterDownload(IDbConnection db, IDbTransaction transaction, ManifestJob request, DepotManifest depotManifest)
        {
            var filesOld = (await db.QueryAsync<DepotFile>("SELECT `ID`, `File`, `Hash`, `Size`, `Flags` FROM `DepotsFiles` WHERE `DepotID` = @DepotID", new { request.DepotID }, transaction: transaction)).ToDictionary(x => x.File, x => x);
            var filesAdded = new List<DepotFile>();
            var shouldHistorize = filesOld.Count > 0; // Don't historize file additions if we didn't have any data before

            foreach (var file in depotManifest.Files)
            {
                var name = file.FileName.Replace('\\', '/');
                byte[] hash = null;

                // Store empty hashes as NULL (e.g. an empty file)
                if (file.FileHash.Length > 0 && (file.Flags & EDepotFileFlag.Directory) == 0)
                {
                    for (var i = 0; i < file.FileHash.Length; ++i)
                    {
                        if (file.FileHash[i] != 0)
                        {
                            hash = file.FileHash;
                            break;
                        }
                    }
                }

                // safe guard
                if (name.Length > 255)
                {
                    ErrorReporter.Notify("Depot Processor", new OverflowException(string.Format("File \"{0}\" in depot {1} is too long", name, request.DepotID)));

                    continue;
                }

                if (filesOld.ContainsKey(name))
                {
                    var oldFile = filesOld[name];
                    var updateFile = false;

                    if (oldFile.Size != file.TotalSize || !Utils.IsEqualSHA1(hash, oldFile.Hash))
                    {
                        await MakeHistory(db, transaction, request, name, "modified", oldFile.Size, file.TotalSize);

                        updateFile = true;
                    }

                    if (oldFile.Flags != file.Flags)
                    {
                        await MakeHistory(db, transaction, request, name, "modified_flags", (ulong)oldFile.Flags, (ulong)file.Flags);

                        updateFile = true;
                    }

                    if (updateFile)
                    {
                        await db.ExecuteAsync("UPDATE `DepotsFiles` SET `Hash` = @Hash, `Size` = @Size, `Flags` = @Flags WHERE `DepotID` = @DepotID AND `ID` = @ID", new DepotFile
                        {
                            ID = oldFile.ID,
                            DepotID = request.DepotID,
                            Hash = hash,
                            Size = file.TotalSize,
                            Flags = file.Flags
                        }, transaction: transaction);
                    }

                    filesOld.Remove(name);
                }
                else
                {
                    // We want to historize modifications first, and only then deletions and additions
                    filesAdded.Add(new DepotFile
                    {
                        DepotID = request.DepotID,
                        Hash = hash,
                        File = name,
                        Size = file.TotalSize,
                        Flags = file.Flags
                    });
                }
            }

            if (filesOld.Count > 0)
            {
                await db.ExecuteAsync("DELETE FROM `DepotsFiles` WHERE `DepotID` = @DepotID AND `ID` IN @Files", new { request.DepotID, Files = filesOld.Select(x => x.Value.ID) }, transaction: transaction);
                await db.ExecuteAsync(HistoryQuery, filesOld.Select(x => new DepotHistory
                {
                    DepotID = request.DepotID,
                    ChangeID = request.ChangeNumber,
                    Action = "removed",
                    File = x.Value.File,
                    OldValue = x.Value.Size
                }), transaction: transaction);
            }

            if (filesAdded.Count > 0)
            {
                await db.ExecuteAsync("INSERT INTO `DepotsFiles` (`DepotID`, `File`, `Hash`, `Size`, `Flags`) VALUES (@DepotID, @File, @Hash, @Size, @Flags)", filesAdded, transaction: transaction);

                if (shouldHistorize)
                {
                    await db.ExecuteAsync(HistoryQuery, filesAdded.Select(x => new DepotHistory
                    {
                        DepotID = request.DepotID,
                        ChangeID = request.ChangeNumber,
                        Action = "added",
                        File = x.File,
                        NewValue = x.Size
                    }), transaction: transaction);
                }
            }

            await db.ExecuteAsync("UPDATE `Depots` SET `LastManifestID` = @ManifestID, `LastUpdated` = CURRENT_TIMESTAMP() WHERE `DepotID` = @DepotID", new { request.DepotID, request.ManifestID }, transaction: transaction);

            return EResult.OK;
        }

        private static Task MakeHistory(IDbConnection db, IDbTransaction transaction, ManifestJob request, string file, string action, ulong oldValue = 0, ulong newValue = 0)
        {
            return db.ExecuteAsync(HistoryQuery,
                new DepotHistory
                {
                    DepotID = request.DepotID,
                    ChangeID = request.ChangeNumber,
                    Action = action,
                    File = file,
                    OldValue = oldValue,
                    NewValue = newValue
                },
                transaction: transaction
            );
        }

        private void RemoveLock(uint depotID)
        {
            lock (DepotLocks)
            {
                if (DepotLocks.Remove(depotID))
                {
                    Log.WriteInfo("Depot Downloader", "Processed depot {0} ({1} depot locks left)", depotID, DepotLocks.Count);
                }
            }
        }

        private CDNClient.Server GetContentServer()
        {
            var i = Utils.NextRandom(CDNServers.Count);

            return CDNServers[i];
        }
    }
}
