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
using System.Text;
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
            public ulong LastManifestID;
            public string DepotName;
            public CDNClient.Server Server;
            public byte[] DepotKey;
            public EResult Result = EResult.Fail;
            public bool StoredFilenamesEncrypted;
        }

        public const string HistoryQuery = "INSERT INTO `DepotsHistory` (`ManifestID`, `ChangeID`, `DepotID`, `File`, `Action`, `OldValue`, `NewValue`) VALUES (@ManifestID, @ChangeID, @DepotID, @File, @Action, @OldValue, @NewValue)";

        private static readonly object UpdateScriptLock = new object();

        private CDNClient CDNClient;
        private readonly Dictionary<uint, byte> DepotLocks;
        private List<CDNClient.Server> CDNServers;
        private readonly string UpdateScript;
        private bool SaveLocalConfig;

        public int DepotLocksCount => DepotLocks.Count;
        public DateTime LastServerRefreshTime { get; private set; } = DateTime.Now;

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

        public async void UpdateContentServerList()
        {
            LastServerRefreshTime = DateTime.Now;

            KeyValue response;

            using (var steamDirectory = Steam.Configuration.GetAsyncWebAPIInterface("IContentServerDirectoryService"))
            {
                steamDirectory.Timeout = TimeSpan.FromSeconds(30);

                try
                {
                    response = await steamDirectory.CallAsync(HttpMethod.Get, "GetServersForSteamPipe", 1,
                        new Dictionary<string, object>
                        {
                            { "cell_id", LocalConfig.Current.CellID },
                            { "max_servers", "100" }
                        });

                    if (response["servers"] == KeyValue.Invalid)
                    {
                        throw new Exception("response.servers is invalid");
                    }
                }
                catch (Exception e)
                {
                    Log.WriteError("Depot Processor", $"Failed to get server list: {e.Message}");

                    return;
                }
            }

            var newServers = new List<CDNClient.Server>();

            foreach (var server in response["servers"].Children)
            {
                if (server["type"].AsString() != "SteamCache" || server["https_support"].AsString() == "mandatory")
                {
                    continue;
                }

                newServers.Add(new DnsEndPoint(server["host"].AsString(), 80));
            }

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
                };

                // Ignore keys that aren't integers, for example "branches"
                if (!uint.TryParse(depot.Name, out request.DepotID))
                {
                    continue;
                }

                request.DepotName = depot["name"].AsString() ?? $"SteamDB Unnamed Depot {request.DepotID}";

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
                        await using var db = await Database.GetConnectionAsync();
                        await db.ExecuteAsync("INSERT INTO `Depots` (`DepotID`, `Name`) VALUES (@DepotID, @DepotName) ON DUPLICATE KEY UPDATE `DepotID` = VALUES(`DepotID`)", new { request.DepotID, request.DepotName });

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

            await using (var db = await Database.GetConnectionAsync())
            {
                await db.ExecuteAsync("INSERT INTO `Builds` (`BuildID`, `ChangeID`, `AppID`) VALUES (@BuildID, @ChangeNumber, @AppID) ON DUPLICATE KEY UPDATE `AppID` = VALUES(`AppID`)",
                new
                {
                    requests[0].BuildID,
                    requests[0].ChangeNumber,
                    appID
                });

                var depotIds = requests.Select(x => x.DepotID).ToList();
                var dbDepots = (await db.QueryAsync<Depot>("SELECT `DepotID`, `Name`, `BuildID`, `ManifestID`, `LastManifestID`, `FilenamesEncrypted` FROM `Depots` WHERE `DepotID` IN @depotIds", new { depotIds }))
                    .ToDictionary(x => x.DepotID, x => x);

                var decryptionKeys = (await db.QueryAsync<DepotKey>("SELECT `DepotID`, `Key` FROM `DepotsKeys` WHERE `DepotID` IN @depotIds", new { depotIds }))
                    .ToDictionary(x => x.DepotID, x => Utils.StringToByteArray(x.Key));

                foreach (var request in requests)
                {
                    Depot dbDepot;

                    decryptionKeys.TryGetValue(request.DepotID, out request.DepotKey);

                    if (dbDepots.ContainsKey(request.DepotID))
                    {
                        dbDepot = dbDepots[request.DepotID];

                        if (dbDepot.BuildID > request.BuildID)
                        {
                            // buildid went back in time? this either means a rollback, or a shared depot that isn't synced properly

                            Log.WriteDebug("Depot Processor", "Skipping depot {0} due to old buildid: {1} > {2}", request.DepotID, dbDepot.BuildID, request.BuildID);

                            continue;
                        }

                        if (dbDepot.LastManifestID == request.ManifestID
                        && dbDepot.ManifestID == request.ManifestID
                        && Settings.Current.FullRun != FullRunState.WithForcedDepots
                        && !dbDepot.FilenamesEncrypted && request.DepotKey != null)
                        {
                            // Update depot name if changed
                            if (request.DepotName != dbDepot.Name)
                            {
                                await db.ExecuteAsync("UPDATE `Depots` SET `Name` = @DepotName WHERE `DepotID` = @DepotID", new { request.DepotID, request.DepotName });
                            }

                            continue;
                        }

                        request.StoredFilenamesEncrypted = dbDepot.FilenamesEncrypted;
                        request.LastManifestID = dbDepot.LastManifestID;
                    }
                    else
                    {
                        dbDepot = new Depot();
                    }

                    if (dbDepot.BuildID != request.BuildID || dbDepot.ManifestID != request.ManifestID || request.DepotName != dbDepot.Name)
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

        private static async Task GetDepotDecryptionKey(SteamApps instance, ManifestJob depot, uint appID)
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

            await using (var db = await Database.GetConnectionAsync())
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
            var willDownloadFiles = false;

            foreach (var depot in depots)
            {
                if (depot.DepotKey == null)
                {
                    await GetDepotDecryptionKey(Steam.Instance.Apps, depot, appID);

                    if (depot.DepotKey == null && depot.LastManifestID == depot.ManifestID)
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

                    RemoveErroredServer(depot.Server);

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

                willDownloadFiles = true;

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

            Log.WriteDebug("Depot Downloader", $"{depots.Count} depot downloads finished for app {appID}");

            // TODO: use ContinueWith on tasks
            if (!anyFilesDownloaded && !willDownloadFiles)
            {
                foreach (var depot in depots)
                {
                    RemoveLock(depot.DepotID);
                }

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

                        // Mark this depot for redownload
                        var db = Database.Get();
                        db.Execute("UPDATE `Depots` SET `LastManifestID` = 0 WHERE `DepotID` = @DepotID", new { depot.DepotID });
                    }

                    RemoveLock(depot.DepotID);
                }

                // Only commit changes if all depots downloaded
                if (processTasks.All(x => x.Result == EResult.OK || x.Result == EResult.Ignored))
                {
                    if (!RunUpdateScriptForApp(appID, depots[0].BuildID))
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

        private static bool RunUpdateScript(string script, string arg)
        {
            if (!File.Exists(script))
            {
                return false;
            }

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

            return true;
        }

        private static bool RunUpdateScriptForApp(uint appID, int buildID)
        {
            var downloadFolder = FileDownloader.GetAppDownloadFolder(appID);

            if (downloadFolder == null)
            {
                return false;
            }

            var updateScript = Path.Combine(Application.Path, "files", downloadFolder, "update.sh");

            return RunUpdateScript(updateScript, buildID.ToString());
        }

        private static async Task<EResult> ProcessDepotAfterDownload(ManifestJob request, DepotManifest depotManifest)
        {
            await using var db = await Database.GetConnectionAsync();
            await using var transaction = await db.BeginTransactionAsync();

            var result = await ProcessDepotAfterDownload(db, transaction, request, depotManifest);
            await transaction.CommitAsync();
            return result;
        }

        private static async Task<EResult> ProcessDepotAfterDownload(IDbConnection db, IDbTransaction transaction, ManifestJob request, DepotManifest depotManifest)
        {
            var filesOld = (await db.QueryAsync<DepotFile>("SELECT `File`, `Hash`, `Size`, `Flags` FROM `DepotsFiles` WHERE `DepotID` = @DepotID", new { request.DepotID }, transaction: transaction)).ToDictionary(x => x.File, x => x);
            var filesAdded = new List<DepotFile>();
            var shouldHistorize = filesOld.Count > 0 && !depotManifest.FilenamesEncrypted; // Don't historize file additions if we didn't have any data before

            if (request.StoredFilenamesEncrypted && !depotManifest.FilenamesEncrypted)
            {
                Log.WriteInfo(nameof(DepotProcessor), $"Depot {request.DepotID} will decrypt stored filenames");

                var decryptedFilesOld = new Dictionary<string, DepotFile>();

                foreach (var file in filesOld.Values)
                {
                    var oldFile = file.File;
                    file.File = DecryptFilename(oldFile, request.DepotKey);

                    decryptedFilesOld.Add(file.File, file);

                    await db.ExecuteAsync("UPDATE `DepotsFiles` SET `File` = @File WHERE `DepotID` = @DepotID AND `File` = @OldFile", new
                    {
                        request.DepotID,
                        file.File,
                        OldFile = oldFile
                    }, transaction);
                }

                filesOld = decryptedFilesOld;

#if false
                var history = await db.QueryAsync<DepotHistory>("SELECT `ID`, `File` FROM `DepotsHistory` WHERE `DepotID` = @DepotID", new { request.DepotID }, transaction);

                foreach (var file in history)
                {
                    if (file.File.Length == 0)
                    {
                        continue;
                    }

                    file.File = DecryptFilename(file.File, request.DepotKey);

                    await db.ExecuteAsync("UPDATE `DepotsHistory` SET `File` = @File WHERE `ID` = @ID", new { file.ID, file.File }, transaction);
                }
#endif
            }

            foreach (var file in depotManifest.Files.OrderByDescending(x => x.FileName))
            {
                var name = depotManifest.FilenamesEncrypted ? file.FileName.Replace("\n", "") : file.FileName.Replace('\\', '/');

                byte[] hash = null;

                // Store empty hashes as NULL (e.g. an empty file)
                if ((file.Flags & EDepotFileFlag.Directory) == 0 && file.FileHash.Length > 0 && file.FileHash.Any(c => c != 0))
                {
                    hash = file.FileHash;
                }

                // Limit path names to 260 characters (default windows max length)
                // File column is varchar(260) and not higher to prevent reducing performance
                // See https://stackoverflow.com/questions/1962310/importance-of-varchar-length-in-mysql-table/1962329#1962329
                // Until 2019 there hasn't been a single file that went over this limit, so far there has been only one
                // game with a big node_modules path, so we're safeguarding by limiting it.
                if (name.Length > 260)
                {
                    if (depotManifest.FilenamesEncrypted)
                    {
                        continue;
                    }

                    using var sha = new System.Security.Cryptography.SHA1Managed();
                    var nameHash = Utils.ByteArrayToString(sha.ComputeHash(Encoding.UTF8.GetBytes(name)));
                    name = $"{{SteamDB file name is too long}}/{nameHash}/...{name.Substring(name.Length - 150)}";
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
                        await db.ExecuteAsync("UPDATE `DepotsFiles` SET `Hash` = @Hash, `Size` = @Size, `Flags` = @Flags WHERE `DepotID` = @DepotID AND `File` = @File", new DepotFile
                        {
                            DepotID = request.DepotID,
                            File = name,
                            Hash = hash,
                            Size = file.TotalSize,
                            Flags = file.Flags
                        }, transaction);
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
                await db.ExecuteAsync("DELETE FROM `DepotsFiles` WHERE `DepotID` = @DepotID AND `File` IN @Files", new { request.DepotID, Files = filesOld.Select(x => x.Value.File) }, transaction);

                if (!depotManifest.FilenamesEncrypted)
                {
                    await db.ExecuteAsync(HistoryQuery, filesOld.Select(x => new DepotHistory
                    {
                        DepotID = request.DepotID,
                        ManifestID = request.ManifestID,
                        ChangeID = request.ChangeNumber,
                        Action = "removed",
                        File = x.Value.File,
                        OldValue = x.Value.Size
                    }), transaction);
                }
            }

            if (filesAdded.Count > 0)
            {
                await db.ExecuteAsync("INSERT INTO `DepotsFiles` (`DepotID`, `File`, `Hash`, `Size`, `Flags`) VALUES (@DepotID, @File, @Hash, @Size, @Flags)", filesAdded, transaction: transaction);

                if (shouldHistorize)
                {
                    await db.ExecuteAsync(HistoryQuery, filesAdded.Select(x => new DepotHistory
                    {
                        DepotID = request.DepotID,
                        ManifestID = request.ManifestID,
                        ChangeID = request.ChangeNumber,
                        Action = "added",
                        File = x.File,
                        NewValue = x.Size
                    }), transaction);
                }
            }

            await db.ExecuteAsync(
                "UPDATE `Depots` SET `LastManifestID` = @ManifestID, `LastUpdated` = CURRENT_TIMESTAMP(), `FilenamesEncrypted` = @FilenamesEncrypted WHERE `DepotID` = @DepotID",
                new
                {
                    request.DepotID,
                    request.ManifestID,
                    depotManifest.FilenamesEncrypted,
                }, transaction);

            return EResult.OK;
        }

        private static string DecryptFilename(string name, byte[] depotKey)
        {
            var encryptedFilename = Convert.FromBase64String(name);
            var decryptedFilename = CryptoHelper.SymmetricDecrypt(encryptedFilename, depotKey);

            return Encoding.UTF8.GetString(decryptedFilename).TrimEnd(new[] { '\0' }).Replace('\\', '/');
        }

        private static Task MakeHistory(IDbConnection db, IDbTransaction transaction, ManifestJob request, string file, string action, ulong oldValue = 0, ulong newValue = 0)
        {
            return db.ExecuteAsync(HistoryQuery,
                new DepotHistory
                {
                    DepotID = request.DepotID,
                    ManifestID = request.ManifestID,
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

        private void RemoveErroredServer(CDNClient.Server server)
        {
            // Let the watchdog update the server list in next check
            LastServerRefreshTime = DateTime.MinValue;

            Log.WriteWarn("Depot Downloader", $"Removing {server.ToString()} due to a download error");

            CDNServers.Remove(server);

            // Always have one server in the list in case we run out
            if (CDNServers.Count == 0)
            {
                CDNServers.Add(new DnsEndPoint("valve500.steamcontent.com", 80));
            }
        }

        private CDNClient.Server GetContentServer()
        {
            var i = Utils.NextRandom(CDNServers.Count);

            return CDNServers[i];
        }
    }
}
