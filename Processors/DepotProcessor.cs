/*
 * Copyright (c) 2013, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Amib.Threading;
using MySql.Data.MySqlClient;
using Newtonsoft.Json;
using SteamKit2;

namespace SteamDatabaseBackend
{
    public static class DepotProcessor
    {
        private const string FILES_DIRECTORY = "files";

        private sealed class DepotFile
        {
            public string Name;
            public ulong Size;
            public int Chunks;
            public int Flags;
        }

        private sealed class ManifestJob
        {
            public JobID JobID;
            public uint ChangeNumber;
            public uint ParentAppID;
            public uint DepotID;
            public ulong ManifestID;
            public string DepotName;
            public byte[] Ticket;
            public byte[] DepotKey;
        }

        private static List<ManifestJob> ManifestJobs;
        public static SmartThreadPool ThreadPool { get; private set; }

        public static void Init()
        {
            ManifestJobs = new List<ManifestJob>();

            ThreadPool = new SmartThreadPool();
            ThreadPool.Name = "Depot Processor Pool";

            Steam.Instance.CallbackManager.Register(new JobCallback<SteamApps.AppOwnershipTicketCallback>(OnAppOwnershipTicket));
            Steam.Instance.CallbackManager.Register(new JobCallback<SteamApps.DepotKeyCallback>(OnDepotKeyCallback));

            if (Settings.Current.ImportantFiles.Count > 0)
            {
                try
                {
                    Directory.CreateDirectory(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FILES_DIRECTORY));
                }
                catch (Exception ex)
                {
                    Log.WriteError("Important Files", "Unable to create directory: {0}", ex.Message);
                }
            }
        }

        public static void Process(uint appID, uint changeNumber, KeyValue depots)
        {
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

                lock (ManifestJobs)
                {
                    if (ManifestJobs.Find(r => r.DepotID == depotID) != null)
                    {
                        // If we already have this depot in our job list, ignore it
                        continue;
                    }
                }

                ulong manifestID;

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

                    continue;
                }

                if (Settings.Current.FullRun != 3)
                {
                    // Check if manifestid in our database is equal
                    using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `ManifestID` FROM `Depots` WHERE `DepotID` = @DepotID AND `Files` != '' LIMIT 1", new MySqlParameter("DepotID", depotID)))
                    {
                        if (Reader.Read() && Reader.GetUInt64("ManifestID") == manifestID)
                        {
                            continue;
                        }
                    }
                }

                Log.WriteInfo("Depot Processor", "DepotID: {0}", depotID);

                var request = new ManifestJob
                {
                    ChangeNumber = changeNumber,
                    ParentAppID = appID,
                    DepotID = depotID,
                    ManifestID = manifestID,
                    DepotName = depot["name"].AsString()
                };

                lock (ManifestJobs)
                {
                    ManifestJobs.Add(request);
                }

                request.JobID = Steam.Instance.Apps.GetAppOwnershipTicket(depotID);
            }
        }

        private static void OnAppOwnershipTicket(SteamApps.AppOwnershipTicketCallback callback, JobID jobID)
        {
            ManifestJob request;

            lock (ManifestJobs)
            {
                request = ManifestJobs.Find(r => r.JobID == jobID);
            }

            if (request == null)
            {
                return;
            }

            if (callback.Result != EResult.OK)
            {
                lock (ManifestJobs)
                {
                    ManifestJobs.Remove(request);
                }

                if (callback.Result != EResult.AccessDenied)
                {
                    Log.WriteWarn("Depot Processor", "Failed to get app ticket for depot {0} (parent {1}) - {2}", callback.AppID, request.ParentAppID, callback.Result);
                }

                return;
            }

            request.Ticket = callback.Ticket;
            request.JobID = Steam.Instance.Apps.GetDepotDecryptionKey(callback.AppID, request.ParentAppID);
        }

        private static void OnDepotKeyCallback(SteamApps.DepotKeyCallback callback, JobID jobID)
        {
            ManifestJob request;

            lock (ManifestJobs)
            {
                request = ManifestJobs.Find(r => r.JobID == jobID);
            }

            if (request == null)
            {
                return;
            }

            if (callback.Result != EResult.OK)
            {
                lock (ManifestJobs)
                {
                    ManifestJobs.Remove(request);
                }

                if (callback.Result != EResult.Blocked)
                {
                    Log.WriteWarn("Depot Processor", "Failed to get depot key for depot {0} (parent {1}) - {2}", callback.DepotID, request.ParentAppID, callback.Result);
                }

                return;
            }

            // Update manifestid here because actually downloading the manifest has chances of failing
            DbWorker.ExecuteNonQuery("INSERT INTO `Depots` (`DepotID`, `Name`, `ManifestID`) VALUES (@DepotID, @Name, @ManifestID) ON DUPLICATE KEY UPDATE `LastUpdated` = CURRENT_TIMESTAMP(), `Name` = @Name, `ManifestID` = @ManifestID",
                                     new MySqlParameter("@DepotID", request.DepotID),
                                     new MySqlParameter("@ManifestID", request.ManifestID),
                                     new MySqlParameter("@Name", request.DepotName)
            );

            request.DepotKey = callback.DepotKey;

            ThreadPool.QueueWorkItem(DownloadManifest, request);
        }

        private static void DownloadManifest(ManifestJob request)
        {
            CDNClient cdnClient = new CDNClient(Steam.Instance.Client, request.DepotID, request.Ticket, request.DepotKey);
            List<CDNClient.Server> cdnServers;

            try
            {
                cdnServers = cdnClient.FetchServerList();

                if (cdnServers.Count == 0)
                {
                    throw new InvalidOperationException("No servers returned"); // Great programming!
                }
            }
            catch
            {
                Log.WriteError("Depot Processor", "Failed to get server list for depot {0}", request.DepotID);

                lock (ManifestJobs)
                {
                    ManifestJobs.Remove(request);
                }

                return;
            }

            DepotManifest depotManifest = null;

            foreach (var server in cdnServers)
            {
                try
                {
                    cdnClient.Connect(server);

                    depotManifest = cdnClient.DownloadManifest(request.ManifestID);

                    break;
                }
                catch { }
            }

            lock (ManifestJobs)
            {
                ManifestJobs.Remove(request);
            }

            if (depotManifest == null)
            {
                Log.WriteError("Depot Processor", "Failed to download depot manifest for depot {0} (parent {1}) (jobs still in queue: {2})", request.DepotID, request.ParentAppID, ManifestJobs.Count);

                if (SteamProxy.Instance.ImportantApps.Contains(request.ParentAppID))
                {
                    IRC.SendMain("Important manifest update: {0}{1}{2} {3}(parent {4}){5} -{6} manifest download failed from {7} servers", Colors.OLIVE, request.DepotName, Colors.NORMAL, Colors.DARK_GRAY, request.ParentAppID, Colors.NORMAL, Colors.RED, cdnServers.Count);
                }

                return;
            }

            if (SteamProxy.Instance.ImportantApps.Contains(request.ParentAppID))
            {
                IRC.SendMain("Important manifest update: {0}{1}{2} {3}(parent {4}){5} -{6} {7}", Colors.OLIVE, request.DepotName, Colors.NORMAL, Colors.DARK_GRAY, request.ParentAppID, Colors.NORMAL, Colors.DARK_BLUE, SteamDB.GetDepotURL(request.DepotID, "history"));
            }

            var sortedFiles = depotManifest.Files.OrderBy(f => f.FileName, StringComparer.OrdinalIgnoreCase);

            bool shouldHistorize = false;
            List<DepotFile> filesNew = new List<DepotFile>();
            List<DepotFile> filesOld = new List<DepotFile>();

            foreach (var file in sortedFiles)
            {
                filesNew.Add(new DepotFile
                {
                    Name = file.FileName.Replace('\\', '/'),
                    Size = file.TotalSize,
                    Chunks = file.Chunks.Count,
                    Flags = (int)file.Flags
                });
            }

            using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `Files` FROM `Depots` WHERE `DepotID` = @DepotID LIMIT 1", new MySqlParameter("DepotID", request.DepotID)))
            {
                if (Reader.Read())
                {
                    string files = Reader.GetString("Files");

                    if (!string.IsNullOrEmpty(files))
                    {
                        shouldHistorize = true;
                        filesOld = JsonConvert.DeserializeObject<List<DepotFile>>(files);
                    }
                }
            }

            DbWorker.ExecuteNonQuery("UPDATE `Depots` SET `Files` = @Files WHERE `DepotID` = @DepotID",
                                     new MySqlParameter("@DepotID", request.DepotID),
                                     new MySqlParameter("@Files", JsonConvert.SerializeObject(filesNew, new JsonSerializerSettings { DefaultValueHandling = DefaultValueHandling.Ignore }))
            );

            if (shouldHistorize)
            {
                List<string> filesAdded = new List<string>();

                foreach (var file in filesNew)
                {
                    var oldFile = filesOld.Find(x => x.Name == file.Name);

                    if (oldFile == null)
                    {
                        // We want to historize modifications first, and only then deletions and additions
                        filesAdded.Add(file.Name);
                    }
                    else
                    {
                        if (oldFile.Size != file.Size)
                        {
                            MakeHistory(request, file.Name, "modified", oldFile.Size, file.Size);
                        }

                        filesOld.Remove(oldFile);
                    }
                }

                foreach (var file in filesOld)
                {
                    MakeHistory(request, file.Name, "removed");
                }

                foreach (string file in filesAdded)
                {
                    MakeHistory(request, file, "added");
                }
            }

            lock (ManifestJobs)
            {
                Log.WriteDebug("Depot Processor", "DepotID: Processed {0} (jobs still in queue: {1})", request.DepotID, ManifestJobs.Count);
            }

            // Our job is done here, now download important files if any
            if (Settings.Current.ImportantFiles.ContainsKey(request.DepotID))
            {
                // TODO: Horribly inefficient
                var importantFiles = sortedFiles.Where(x => Settings.Current.ImportantFiles[request.DepotID].Contains(x.FileName.Replace('\\', '/')));

                ThreadPool.QueueWorkItem(DownloadFiles, importantFiles, request.DepotID, cdnClient);
            }
        }

        private static void DownloadFiles(IEnumerable<DepotManifest.FileData> importantFiles, uint DepotID, CDNClient cdnClient)
        {
            foreach (var file in importantFiles)
            {
                Log.WriteDebug("Important Files", "Downloading {0} ({1} bytes, {2} chunks)", file.FileName, file.TotalSize, file.Chunks.Count);

                string downloadPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FILES_DIRECTORY, string.Format("{0}_{1}", DepotID, file.FileName.Replace('\\', '/').Replace('/', '_')));

                using (FileStream fs = File.Open(downloadPath, FileMode.OpenOrCreate))
                {
                    fs.SetLength((long)file.TotalSize);

                    foreach (var chunk in file.Chunks)
                    {
                        try
                        {
                            var chunkData = cdnClient.DownloadDepotChunk(chunk);

                            fs.Seek((long)chunk.Offset, SeekOrigin.Begin);
                            fs.Write(chunkData.Data, 0, chunkData.Data.Length);

                            Log.WriteDebug("Important Files", "Downloaded {0}", file.FileName);

                            break;
                        }
                        catch (Exception e)
                        {
                            Log.WriteDebug("Important Files", "Fail {0}", e.Message);
                        }
                    }
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
    }
}
