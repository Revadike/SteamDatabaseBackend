#if DEBUG
/*
 * Copyright (c) 2013, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
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
        private class DepotFile
        {
            public string Name;
            public ulong Size;
            public int Chunks;
            public int Flags;
        }

        private class ManifestJob
        {
            public JobID JobID;
            public uint ChangeNumber;
            public uint ParentAppID;
            public uint DepotID;
            public ulong ManifestID;
            public string DepotName;
            public byte[] Ticket;
        }

        private static int NextServer;
        private static List<CDNClient.ClientEndPoint> cdnServers;
        private static List<ManifestJob> ManifestJobs;

        public static SmartThreadPool ThreadPool;

        public static void Init()
        {
            ManifestJobs = new List<ManifestJob>();

            ThreadPool = new SmartThreadPool();
            ThreadPool.Name = "Depot Processor Pool";

            new JobCallback<SteamApps.AppOwnershipTicketCallback>(OnAppOwnershipTicket, Steam.Instance.CallbackManager);
            new JobCallback<SteamApps.DepotKeyCallback>(OnDepotKeyCallback, Steam.Instance.CallbackManager);
        }

        public static void FetchServers()
        {
            // TODO: All the code below should be gone with VoiDeD's refacted CDNClient

            var csServers = Steam.Instance.Client.GetServersOfType(EServerType.CS);

            for (int attempt = 0; attempt < 10; attempt++)
            {
                var csServer = csServers[new Random().Next(csServers.Count)];

                cdnServers = CDNClient.FetchServerList(new CDNClient.ClientEndPoint(csServer.Address.ToString(), csServer.Port), Steam.Instance.CellID);

                if (cdnServers != null)
                {
                    break;
                }
            }

            if (cdnServers == null)
            {
                Log.WriteWarn("Depot Processor", "Unable to get CDN server list");
                return;
            }

            cdnServers = cdnServers.Where(ep => ep.Type == "CS").ToList();
        }

        public static void Process(uint AppID, uint ChangeNumber, KeyValue depots)
        {
            foreach (KeyValue depot in depots.Children)
            {
                uint DepotID;

                if (!uint.TryParse(depot.Name, out DepotID))
                {
                    // Ignore keys that aren't integers, for example "branches"
                    continue;
                }

                if (ManifestJobs.Find(r => r.DepotID == DepotID) != null)
                {
                    // If we already have this depot in our job list, ignore it
                    continue;
                }

                ulong ManifestID = (ulong)depot["manifests"]["public2"].AsLong();

                // If there is no public manifest for this depot, it still could have some sort of open beta
                if (ManifestID == 0)
                {
                    var ManifestID2 = depot["manifests"].Children.FirstOrDefault();

                    if (ManifestID2 == null)
                    {
                        // There are no public manifests
                        continue;
                    }

                    ManifestID = (ulong)ManifestID2.AsLong();

                    if (ManifestID == 0)
                    {
                        // Still nope
                        continue;
                    }
                }

                // Check if manifestid in our database is equal
                using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `ManifestID` FROM `Depots` WHERE `DepotID` = @DepotID LIMIT 1", new MySqlParameter("DepotID", DepotID)))
                {
                    if (Reader.Read() && Reader.GetUInt64("ManifestID") == ManifestID)
                    {
                        continue;
                    }
                }

#if DEBUG
                Log.WriteDebug("Depot Processor", "DepotID: {0}", DepotID);
#endif

                var jobID = Steam.Instance.Apps.GetAppOwnershipTicket(DepotID);

                ManifestJobs.Add(new ManifestJob
                {
                    JobID = jobID,
                    ChangeNumber = ChangeNumber,
                    ParentAppID = AppID,
                    DepotID = DepotID,
                    ManifestID = ManifestID,
                    DepotName = depot["name"].AsString()
                });
            }
        }

        public static void OnAppOwnershipTicket(SteamApps.AppOwnershipTicketCallback callback, JobID jobID)
        {
            var request = ManifestJobs.Find(r => r.JobID == jobID);

            if (request == null)
            {
                return;
            }

            if (callback.Result != EResult.OK)
            {
                ManifestJobs.Remove(request);

                if (callback.Result != EResult.AccessDenied)
                {
                    Log.WriteWarn("Depot Processor", "Failed to get app ticket for {0} - {1}", callback.AppID, callback.Result);
                }

                return;
            }

            request.Ticket = callback.Ticket;
            request.JobID = Steam.Instance.Apps.GetDepotDecryptionKey(callback.AppID, request.ParentAppID);
        }

        public static void OnDepotKeyCallback(SteamApps.DepotKeyCallback callback, JobID jobID)
        {
            var request = ManifestJobs.Find(r => r.JobID == jobID);

            if (request == null)
            {
                return;
            }

            ManifestJobs.Remove(request);

            if (callback.Result != EResult.OK)
            {
                if (callback.Result != EResult.Blocked)
                {
                    Log.WriteWarn("Depot Processor", "Failed to get depot key for {0} - {1}", callback.DepotID, callback.Result);
                }

                return;
            }

            if (SteamProxy.Instance.ImportantApps.Contains(request.ParentAppID))
            {
                IRC.SendMain("Important manifest update: {0}{1}{2} -{3} {4}", Colors.OLIVE, request.DepotName, Colors.NORMAL, Colors.DARK_BLUE, SteamDB.GetDepotURL(request.DepotID, "history"));
            }

            ThreadPool.QueueWorkItem(delegate
            {
                uint retries = 5;

                CDNClient cdnClient;
                bool isConnected;

                do
                {
                    NextServer++;

                    if (NextServer > cdnServers.Count)
                    {
                        NextServer = 0;
                    }

                    cdnClient = new CDNClient(cdnServers[NextServer], request.Ticket);

                    isConnected = cdnClient.Connect();
                }
                while (!isConnected && retries-- > 0);

                if (!isConnected)
                {
                    Log.WriteWarn("Depot Processor", "Failed to connect to any CDN for {0}", request.DepotID);
                    return;
                }

                retries = 3;

                DepotManifest depotManifest;

                do
                {
                    depotManifest = cdnClient.DownloadDepotManifest((int)request.DepotID, request.ManifestID);
                }
                while (depotManifest == null && retries-- > 0);

                if (depotManifest == null)
                {
                    Log.WriteWarn("Depot Processor", "Failed to download depot manifest for {0}", request.DepotID);
                    return;
                }

                if (!depotManifest.DecryptFilenames(callback.DepotKey))
                {
                    Log.WriteWarn("Depot Processor", "Failed to decrypt filenames for {0}", request.DepotID);
                    return;
                }

                var sortedFiles = depotManifest.Files.OrderBy(f => f.FileName, StringComparer.OrdinalIgnoreCase);

                bool shouldHistorize = false;
                List<DepotFile> filesNew = new List<DepotFile>();
                List<DepotFile> filesOld = new List<DepotFile>();

                foreach (var file in sortedFiles)
                {
                    filesNew.Add(new DepotFile
                    {
                        Name = file.FileName.Replace("\\", "/"),
                        Size = file.TotalSize,
                        Chunks = file.Chunks.Count,
                        Flags = (int)file.Flags
                    });
                }

                using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `Files` FROM `Depots` WHERE `DepotID` = @DepotID LIMIT 1", new MySqlParameter("DepotID", request.DepotID)))
                {
                    if (Reader.Read())
                    {
                        shouldHistorize = true;
                        filesOld = JsonConvert.DeserializeObject<List<DepotFile>>(Reader.GetString("Files"));
                    }
                }

                DbWorker.ExecuteNonQuery("INSERT INTO `Depots` (`DepotID`, `Name`, `ManifestID`, `Files`) VALUES (@DepotID, @Name, @ManifestID, @Files) ON DUPLICATE KEY UPDATE `LastUpdated` = CURRENT_TIMESTAMP(), `Name` = @Name, `ManifestID` = @ManifestID, `Files` = @Files",
                                         new MySqlParameter("@DepotID", request.DepotID),
                                         new MySqlParameter("@ManifestID", request.ManifestID),
                                         new MySqlParameter("@Files", JsonConvert.SerializeObject(filesNew, new JsonSerializerSettings { DefaultValueHandling = DefaultValueHandling.Ignore })),
                                         new MySqlParameter("@Name", request.DepotName)
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

#if DEBUG
                if (true)
#else
                if (Settings.Current.FullRun > 0)
#endif
                {
                    Log.WriteDebug("Depot Processor", "DepotID: Processed {0}", request.DepotID);
                }
            });
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
#endif