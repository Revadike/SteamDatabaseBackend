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
using System.Security.Cryptography;
using System.Text;
using System.Threading;
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
            public bool DownloadCorrupted;
        }

        public const string HistoryQuery = "INSERT INTO `DepotsHistory` (`ManifestID`, `ChangeID`, `DepotID`, `File`, `Action`, `OldValue`, `NewValue`) VALUES (@ManifestID, @ChangeID, @DepotID, @File, @Action, @OldValue, @NewValue)";

        private static readonly object UpdateScriptLock = new object();
        
        private readonly Dictionary<uint, byte> DepotLocks = new Dictionary<uint, byte>();
        private SemaphoreSlim ManifestDownloadSemaphore = new SemaphoreSlim(15);
        private readonly string UpdateScript;

        private CDNClient CDNClient;
        private List<CDNClient.Server> CDNServers;

        public int DepotLocksCount => DepotLocks.Count;
        public DateTime LastServerRefreshTime { get; private set; } = DateTime.Now;

        public DepotProcessor(SteamClient client)
        {
            UpdateScript = Path.Combine(Application.Path, "files", "update.sh");
            CDNClient = new CDNClient(client);
            CDNServers = new List<CDNClient.Server>
            {
                new DnsEndPoint("valve500.steamcontent.com", 80)
            };

            CDNClient.RequestTimeout = TimeSpan.FromSeconds(30);

            FileDownloader.SetCDNClient(CDNClient);
        }

        public void Dispose()
        {
            if (CDNClient != null)
            {
                CDNClient.Dispose();
                CDNClient = null;
            }

            if (ManifestDownloadSemaphore != null)
            {
                ManifestDownloadSemaphore.Dispose();
                ManifestDownloadSemaphore = null;
            }
        }

        public async Task UpdateContentServerList()
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
                            { "cell_id", Steam.Instance.Client.CellID },
                            { "max_servers", "100" }
                        });

                    if (response["servers"] == KeyValue.Invalid)
                    {
                        throw new Exception("response.servers is invalid");
                    }
                }
                catch (Exception e)
                {
                    Log.WriteError(nameof(DepotProcessor), $"Failed to get server list: {e.Message}");

                    return;
                }
            }

            var newServers = new List<CDNClient.Server>();

            foreach (var server in response["servers"].Children)
            {
                if (server["allowed_app_ids"] != KeyValue.Invalid)
                {
                    continue;
                }

                if (server["type"].AsString() != "SteamCache" && server["type"].AsString() != "CDN")
                {
                    continue;
                }

                newServers.Add(new DnsEndPoint(server["host"].AsString(), server["https_support"].AsString() == "mandatory" ? 443 : 80));
            }

            if (newServers.Count > 0)
            {
                CDNServers = newServers;
            }

            Log.WriteInfo(nameof(DepotProcessor), $"Received {newServers.Count} download servers");
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

                request.DepotName = depot["name"].AsString();

                if (string.IsNullOrEmpty(request.DepotName))
                {
                    request.DepotName = $"SteamDB Unnamed Depot {request.DepotID}";
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
                        await using var db = await Database.GetConnectionAsync();
                        await db.ExecuteAsync("INSERT INTO `Depots` (`DepotID`, `Name`) VALUES (@DepotID, @DepotName) ON DUPLICATE KEY UPDATE `DepotID` = VALUES(`DepotID`)", new { request.DepotID, request.DepotName });

                        continue;
                    }

                    Log.WriteDebug(nameof(DepotProcessor), $"Depot {request.DepotID} (from {appID}) has no public branch, but there is another one");

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

                            Log.WriteDebug(nameof(DepotProcessor), $"Skipping depot {request.DepotID} due to old buildid: {dbDepot.BuildID} > {request.BuildID}");

                            continue;
                        }

                        if (dbDepot.LastManifestID == request.ManifestID
                        && dbDepot.ManifestID == request.ManifestID
                        && Settings.FullRun != FullRunState.WithForcedDepots
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
                            Log.WriteWarn(nameof(DepotProcessor), $"Depot {request.DepotID} was locked in another thread");
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
                        ErrorReporter.Notify(nameof(DepotProcessor), e);
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
                Log.WriteWarn(nameof(DepotProcessor), $"Decryption key timed out for {depot.DepotID}");

                return;
            }

            if (callback.Result != EResult.OK)
            {
                if (callback.Result != EResult.AccessDenied)
                {
                    Log.WriteWarn(nameof(DepotProcessor), $"No access to depot {depot.DepotID} ({callback.Result})");
                }

                return;
            }

            Log.WriteDebug(nameof(DepotProcessor), $"Got a new depot key for depot {depot.DepotID}");

            await using (var db = await Database.GetConnectionAsync())
            {
                await db.ExecuteAsync("INSERT INTO `DepotsKeys` (`DepotID`, `Key`) VALUES (@DepotID, @Key) ON DUPLICATE KEY UPDATE `Key` = VALUES(`Key`)", new { depot.DepotID, Key = Utils.ByteArrayToString(callback.DepotKey) });
            }

            depot.DepotKey = callback.DepotKey;
        }

        private async Task DownloadDepots(uint appID, List<ManifestJob> depots)
        {
            Log.WriteDebug(nameof(DepotProcessor), $"Will process {depots.Count} depots ({DepotLocks.Count} depot locks left)");

            var processTasks = new List<Task<EResult>>();
            var anyFilesDownloaded = false;
            var willDownloadFiles = false;

            foreach (var depot in depots)
            {
                if (depot.DepotKey == null)
                {
                    await GetDepotDecryptionKey(Steam.Instance.Apps, depot, appID);

                    if (depot.DepotKey == null && (depot.LastManifestID == depot.ManifestID || Settings.Current.OnlyOwnedDepots))
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
                        await ManifestDownloadSemaphore.WaitAsync(TaskManager.TaskCancellationToken.Token).ConfigureAwait(false);

                        depotManifest = await CDNClient.DownloadManifestAsync(depot.DepotID, depot.ManifestID, depot.Server, string.Empty, depot.DepotKey);

                        break;
                    }
                    catch (Exception e)
                    {
                        lastError = e.Message;

                        Log.WriteError(nameof(DepotProcessor), $"Failed to download depot manifest for app {appID} depot {depot.DepotID} ({depot.Server}: {lastError}) (#{i})");
                    }
                    finally
                    {
                        ManifestDownloadSemaphore.Release();
                    }

                    if (depot.DepotKey != null)
                    {
                        RemoveErroredServer(depot.Server);
                    }

                    depot.Server = GetContentServer();

                    if (depotManifest == null && i < 5)
                    {
                        await Task.Delay(Utils.ExponentionalBackoff(i + 1));
                    }
                }

                if (depotManifest == null)
                {
                    RemoveLock(depot.DepotID);

                    if (FileDownloader.IsImportantDepot(depot.DepotID))
                    {
                        IRC.Instance.SendOps($"{Colors.OLIVE}[{depot.DepotName}]{Colors.NORMAL} Failed to download manifest ({lastError})");
                    }

                    if (!Settings.IsFullRun && depot.DepotKey != null)
                    {
                        JobManager.AddJob(() => Steam.Instance.Apps.PICSGetAccessTokens(appID, null));
                    }

                    continue;
                }

                var task = ProcessDepotAfterDownload(depot, depotManifest);

                processTasks.Add(task);

                if (!FileDownloader.IsImportantDepot(depot.DepotID) || depot.DepotKey == null)
                {
                    depot.Result = EResult.Ignored;
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
                });

                processTasks.Add(task);
            }

            await Task.WhenAll(processTasks).ConfigureAwait(false);

            Log.WriteDebug(nameof(DepotProcessor), $"{depots.Count} depot downloads finished for app {appID}");

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
                        RunUpdateScript(UpdateScript, $"{depot.DepotID} no-git");
                    }
                    else if (depot.Result != EResult.Ignored)
                    {
                        Log.WriteWarn(nameof(DepotProcessor), $"Download failed for {depot.DepotID}: {depot.Result}");

                        // Mark this depot for redownload
                        using var db = Database.Get();
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
                    Log.WriteDebug(nameof(DepotProcessor), $"Reprocessing the app {appID} because some files failed to download");

                    IRC.Instance.SendOps($"{Colors.OLIVE}[{Steam.GetAppName(appID)}]{Colors.NORMAL} Reprocessing the app due to download failures");

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

            Log.WriteDebug(nameof(DepotProcessor), $"Running update script: {script} {arg}");

            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = script,
                    Arguments = arg,
                }
            };
            process.Start();
            process.WaitForExit(120000);

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
            if (depotManifest.FilenamesEncrypted && request.DepotKey != null)
            {
                Log.WriteError(nameof(DepotProcessor), $"Depot key for depot {request.DepotID} is invalid?");
                IRC.Instance.SendOps($"[Tokens] Looks like the depot key for depot {request.DepotID} is invalid");
            }

            await using var db = await Database.GetConnectionAsync();
            await using var transaction = await db.BeginTransactionAsync();

            var result = await ProcessDepotAfterDownload(db, transaction, request, depotManifest);
            await transaction.CommitAsync();
            return result;
        }

        private static async Task<EResult> ProcessDepotAfterDownload(IDbConnection db, IDbTransaction transaction, ManifestJob request, DepotManifest depotManifest)
        {
            var filesOld = (await db.QueryAsync<DepotFile>("SELECT `File`, `Hash`, `Size`, `Flags` FROM `DepotsFiles` WHERE `DepotID` = @DepotID", new { request.DepotID }, transaction)).ToDictionary(x => x.File, x => x);
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

                    using var sha = SHA1.Create();
                    var nameHash = Utils.ByteArrayToString(sha.ComputeHash(Encoding.UTF8.GetBytes(name)));
                    name = $"{{SteamDB file name is too long}}/{nameHash}/...{name.Substring(name.Length - 150)}";
                }

                if (filesOld.ContainsKey(name))
                {
                    var oldFile = filesOld[name];
                    var updateFile = false;

                    if (oldFile.Size != file.TotalSize || !Utils.ByteArrayEquals(hash, oldFile.Hash))
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
                // Chunk file deletion queries so it doesn't go over max_allowed_packet
                var filesOldChunks = filesOld.Select(x => x.Value.File).Split(1000);

                foreach (var filesOldChunk in filesOldChunks)
                {
                    await db.ExecuteAsync("DELETE FROM `DepotsFiles` WHERE `DepotID` = @DepotID AND `File` IN @Files",
                        new
                        {
                            request.DepotID,
                            Files = filesOldChunk,
                        }, transaction);
                }

                if (shouldHistorize)
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
                await db.ExecuteAsync("INSERT INTO `DepotsFiles` (`DepotID`, `File`, `Hash`, `Size`, `Flags`) VALUES (@DepotID, @File, @Hash, @Size, @Flags)", filesAdded, transaction);

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
                request.LastManifestID == request.ManifestID ?
                    "UPDATE `Depots` SET `LastManifestID` = @ManifestID, `FilenamesEncrypted` = @FilenamesEncrypted, `SizeOriginal` = @SizeOriginal, `SizeCompressed` = @SizeCompressed WHERE `DepotID` = @DepotID" :
                    "UPDATE `Depots` SET `LastManifestID` = @ManifestID, `FilenamesEncrypted` = @FilenamesEncrypted, `SizeOriginal` = @SizeOriginal, `SizeCompressed` = @SizeCompressed, `LastUpdated` = CURRENT_TIMESTAMP() WHERE `DepotID` = @DepotID",
                new
                {
                    request.DepotID,
                    request.ManifestID,
                    depotManifest.FilenamesEncrypted,
                    SizeOriginal = depotManifest.TotalUncompressedSize,
                    SizeCompressed = depotManifest.TotalCompressedSize,
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
                transaction
            );
        }

        private void RemoveLock(uint depotID)
        {
            lock (DepotLocks)
            {
                if (DepotLocks.Remove(depotID))
                {
                    Log.WriteInfo(nameof(DepotProcessor), $"Processed depot {depotID} ({DepotLocks.Count} depot locks left)");
                }
            }
        }

        private void RemoveErroredServer(CDNClient.Server server)
        {
            if (CDNServers.Count < 10)
            {
                // Let the watchdog update the server list in next check
                LastServerRefreshTime = DateTime.MinValue;
            }

            Log.WriteWarn(nameof(DepotProcessor), $"Removing {server} due to a download error");

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
