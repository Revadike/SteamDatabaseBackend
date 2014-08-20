/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Amib.Threading;
using MySql.Data.MySqlClient;
using Newtonsoft.Json;
using SteamKit2;

namespace SteamDatabaseBackend
{
    public static class DepotProcessor
    {
        private sealed class DepotFile
        {
            public string Hash;
            public string Name;
            public ulong Size;
            public int Chunks;
            public int Flags;
        }

        public class ManifestJob
        {
            public uint ChangeNumber;
            public uint ParentAppID;
            public uint DepotID;
            public ulong ManifestID;
            public ulong PreviousManifestID;
            public string DepotName;
            public string CDNToken;
            public string Server;
            public byte[] DepotKey;
        }

        private static CDNClient CDNClient;
        private static List<string> CDNServers;
        private static ConcurrentDictionary<uint, byte> DepotLocks;
        public static SmartThreadPool ThreadPool { get; private set; }

        public static void Init()
        {
            DepotLocks = new ConcurrentDictionary<uint, byte>();

            ThreadPool = new SmartThreadPool();
            ThreadPool.Name = "Depot Processor Pool";

            Steam.Instance.CallbackManager.Register(new Callback<SteamApps.CDNAuthTokenCallback>(OnCDNAuthTokenCallback));
            Steam.Instance.CallbackManager.Register(new Callback<SteamApps.DepotKeyCallback>(OnDepotKeyCallback));

            CDNClient = new CDNClient(Steam.Instance.Client);

            CDNServers = new List<string>
            {
                "content1.steampowered.com", // Limelight
                "content2.steampowered.com", // Level3
                "content3.steampowered.com", // Highwinds
                //"content4.steampowered.com", // EdgeCast, seems to be missing content
                "content5.steampowered.com", // CloudFront
                //"content6.steampowered.com", // Comcast, non optimal
                "content7.steampowered.com", // Akamai
                "content8.steampowered.com" // Akamai
            };
        }

        public static void Process(uint appID, uint changeNumber, KeyValue depots)
        {
            var buildID = depots["branches"]["public"]["buildid"].AsInteger();

            foreach (KeyValue depot in depots.Children)
            {
                // Ignore these for now, parent app should be updated too anyway
                if (depot["depotfromapp"].Value != null)
                {
                    ////Log.WriteDebug("Depot Processor", "Ignoring depot {0} with depotfromapp value {1} (parent {2})", depot.Name, depot["depotfromapp"].AsString(), AppID);

                    continue;
                }

                uint depotID;

                if (!uint.TryParse(depot.Name, out depotID))
                {
                    // Ignore keys that aren't integers, for example "branches"
                    continue;
                }

                // TODO: instead of locking we could wait for current process to finish
                if (DepotLocks.ContainsKey(depotID))
                {
                    continue;
                }

                ulong manifestID;

                var depotName = depot["name"].AsString();

                if (depot["manifests"]["public"].Value == null || !ulong.TryParse(depot["manifests"]["public"].Value, out manifestID))
                {
#if false
                    Log.WriteDebug("Depot Processor", "Failed to public branch for depot {0} (parent {1}) - {2}", DepotID, AppID);

                    // If there is no public manifest for this depot, it still could have some sort of open beta

                    var branch = depot["manifests"].Children.SingleOrDefault(x => x.Name != "local");

                    if (branch == null || !ulong.TryParse(branch.Value, out ManifestID))
                    {
                        continue;
                    }
#endif

                    DbWorker.ExecuteNonQuery("INSERT INTO `Depots` (`DepotID`, `Name`) VALUES (@DepotID, @Name) ON DUPLICATE KEY UPDATE `LastUpdated` = CURRENT_TIMESTAMP(), `Name` = @Name",
                        new MySqlParameter("@DepotID", depotID),
                        new MySqlParameter("@Name", depotName)
                    );

                    continue;
                }
                    
                var request = new ManifestJob
                {
                    ChangeNumber = changeNumber,
                    ParentAppID = appID,
                    DepotID = depotID,
                    ManifestID = manifestID,
                    DepotName = depotName
                };

                int currentBuildID = 0;

                // Check if manifestid in our database is equal
                using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `Name`, `ManifestID`, `BuildID` FROM `Depots` WHERE `DepotID` = @DepotID LIMIT 1", new MySqlParameter("DepotID", depotID)))
                {
                    if (Reader.Read())
                    {
                        currentBuildID = Reader.GetInt32("buildID");

                        request.PreviousManifestID = Reader.GetUInt64("ManifestID");

                        if (request.PreviousManifestID == manifestID && Settings.Current.FullRun < 2)
                        {
                            // Update depot name if changed
                            if(!depotName.Equals(Reader.GetString("Name")))
                            {
                                DbWorker.ExecuteNonQuery("UPDATE `Depots` SET `Name` = @Name WHERE `DepotID` = @DepotID",
                                    new MySqlParameter("@DepotID", request.DepotID),
                                    new MySqlParameter("@Name", request.DepotName)
                                );
                            }

                            continue;
                        }

                        if (currentBuildID > buildID)
                        {
                            Log.WriteDebug("Depot Processor", "Skipping depot {0} due to old buildid: {1} > {2}", depotID, currentBuildID, buildID);
                            continue;
                        }
                    }
                }

                DepotLocks.TryAdd(depotID, 1);

                // Update/insert depot information straight away
                if (currentBuildID != buildID || request.PreviousManifestID != request.ManifestID)
                {
                    DbWorker.ExecuteNonQuery("INSERT INTO `Depots` (`DepotID`, `Name`, `BuildID`, `ManifestID`) VALUES (@DepotID, @Name, @BuildID, @ManifestID) ON DUPLICATE KEY UPDATE `LastUpdated` = CURRENT_TIMESTAMP(), `Name` = @Name, `BuildID` = @BuildID, `ManifestID` = @ManifestID",
                        new MySqlParameter("@DepotID", request.DepotID),
                        new MySqlParameter("@BuildID", buildID),
                        new MySqlParameter("@ManifestID", request.ManifestID),
                        new MySqlParameter("@Name", request.DepotName)
                    );

                    MakeHistory(request, string.Empty, "manifest_change", request.PreviousManifestID, request.ManifestID);
                }

                request.Server = CDNServers[new Random().Next(CDNServers.Count)];

                JobManager.AddJob(() => Steam.Instance.Apps.GetCDNAuthToken(depotID, request.Server), request);
            }
        }

        private static void OnCDNAuthTokenCallback(SteamApps.CDNAuthTokenCallback callback)
        {
            JobAction job;

            if (!JobManager.TryRemoveJob(callback.JobID, out job))
            {
                return;
            }

            var request = job.ManifestJob;

            if (callback.Result != EResult.OK)
            {
                RemoveLock(request.DepotID);

                return;
            }

            request.CDNToken = callback.Token;

            JobManager.AddJob(() => Steam.Instance.Apps.GetDepotDecryptionKey(request.DepotID, request.ParentAppID), request);
        }

        private static void OnDepotKeyCallback(SteamApps.DepotKeyCallback callback)
        {
            JobAction job;

            if (!JobManager.TryRemoveJob(callback.JobID, out job))
            {
                return;
            }

            var request = job.ManifestJob;

            if (callback.Result != EResult.OK)
            {
                if (callback.Result != EResult.Blocked)
                {
                    Log.WriteError("Depot Processor", "Failed to get depot key for depot {0} (parent {1}) - {2}", callback.DepotID, request.ParentAppID, callback.Result);
                }

                RemoveLock(request.DepotID);

                return;
            }

            Log.WriteInfo("Depot Processor", "DepotID: {0}", request.DepotID);

            request.DepotKey = callback.DepotKey;

            // In full run, process depots after everything else
            if (Settings.IsFullRun)
            {
                Steam.Instance.ProcessorPool.QueueWorkItem(TryDownloadManifest, request, WorkItemPriority.Lowest);
            }
            else
            {
                ThreadPool.QueueWorkItem(TryDownloadManifest, request);
            }
        }

        private static void TryDownloadManifest(ManifestJob request)
        {
            try
            {
                DownloadManifest(request);
            }
            catch (Exception e)
            {
                Log.WriteError("Depot Processor", "Caught exception while processing depot {0}: {1}\n{2}", request.DepotID, e.Message, e.StackTrace);
            }

            Log.WriteDebug("Depot Processor", "Processed depot {0}", request.DepotID);

            RemoveLock(request.DepotID);
        }

        private static void DownloadManifest(ManifestJob request)
        {
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
                Log.WriteError("Depot Processor", "Failed to download depot manifest for depot {0} ({1}: {2})", request.DepotID, request.Server, lastError);

                if (Steam.Instance.ImportantApps.ContainsKey(request.ParentAppID))
                {
                    IRC.SendMain("Important depot update: {0}{1}{2} -{3} failed to download depot manifest", Colors.OLIVE, request.DepotName, Colors.NORMAL, Colors.RED);
                }

                return;
            }

            if (Steam.Instance.ImportantApps.ContainsKey(request.ParentAppID))
            {
                IRC.SendMain("Important depot update: {0}{1}{2} -{3} {4}", Colors.OLIVE, request.DepotName, Colors.NORMAL, Colors.DARK_BLUE, SteamDB.GetDepotURL(request.DepotID, "history"));
            }

            var sortedFiles = depotManifest.Files.OrderBy(f => f.FileName, StringComparer.OrdinalIgnoreCase);

            bool shouldHistorize = false;
            var filesNew = new List<DepotFile>();
            var filesOld = new Dictionary<string, DepotFile>();

            foreach (var file in sortedFiles)
            {
                System.Text.Encoding.UTF8.GetString(file.FileHash);

                var depotFile = new DepotFile
                {
                    Name = file.FileName.Replace('\\', '/'),
                    Size = file.TotalSize,
                    Chunks = file.Chunks.Count,
                    Flags = (int)file.Flags
                };

                // TODO: Ideally we would check if filehash is not empty
                if (!file.Flags.HasFlag(EDepotFileFlag.Directory))
                {
                    depotFile.Hash = string.Concat(Array.ConvertAll(file.FileHash, x => x.ToString("X2")));
                }

                filesNew.Add(depotFile);
            }

            using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `Files` FROM `Depots` WHERE `DepotID` = @DepotID LIMIT 1", new MySqlParameter("DepotID", request.DepotID)))
            {
                if (Reader.Read())
                {
                    string files = Reader.GetString("Files");

                    if (!string.IsNullOrEmpty(files))
                    {
                        shouldHistorize = true;

                        var _filesOld = JsonConvert.DeserializeObject<List<DepotFile>>(files);

                        filesOld = _filesOld.ToDictionary(x => x.Name);
                    }
                }
            }

            DbWorker.ExecuteNonQuery("UPDATE `Depots` SET `Files` = @Files WHERE `DepotID` = @DepotID",
                                     new MySqlParameter("@DepotID", request.DepotID),
                                     new MySqlParameter("@Files", JsonConvert.SerializeObject(filesNew, new JsonSerializerSettings { DefaultValueHandling = DefaultValueHandling.Ignore }))
            );

            if (shouldHistorize)
            {
                var filesAdded = new List<string>();

                foreach (var file in filesNew)
                {
                    if (filesOld.ContainsKey(file.Name))
                    {
                        var oldFile = filesOld[file.Name];

                        if (oldFile.Size != file.Size)
                        {
                            MakeHistory(request, file.Name, "modified", oldFile.Size, file.Size);
                        }
                        else if (file.Hash != null && oldFile.Hash != null && !file.Hash.Equals(oldFile.Hash))
                        {
                            MakeHistory(request, file.Name, "modified", oldFile.Size, file.Size);
                        }

                        filesOld.Remove(file.Name);
                    }
                    else
                    {
                        // We want to historize modifications first, and only then deletions and additions
                        filesAdded.Add(file.Name);
                    }
                }

                foreach (var file in filesOld)
                {
                    MakeHistory(request, file.Value.Name, "removed");
                }

                foreach (string file in filesAdded)
                {
                    MakeHistory(request, file, "added");
                }
            }
        }

        private static void MakeHistory(ManifestJob request, string file, string action, ulong oldValue = 0, ulong newValue = 0)
        {
            DbWorker.ExecuteNonQuery("INSERT INTO `DepotsHistory` (`ChangeID`, `DepotID`, `File`, `Action`, `OldValue`, `NewValue`) VALUES (@ChangeID, @DepotID, @File, @Action, @OldValue, @NewValue)",
                                     new MySqlParameter("@DepotID", request.DepotID),
                                     new MySqlParameter("@ChangeID", request.ChangeNumber),
                                     new MySqlParameter("@File", file),
                                     new MySqlParameter("@Action", action),
                                     new MySqlParameter("@OldValue", oldValue),
                                     new MySqlParameter("@NewValue", newValue)
            );
        }

        private static void RemoveLock(uint depotID)
        {
            byte microsoftWhyIsThereNoRemoveMethodWithoutSecondParam;

            DepotLocks.TryRemove(depotID, out microsoftWhyIsThereNoRemoveMethodWithoutSecondParam);
        }
    }
}
