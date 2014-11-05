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
using SteamKit2;

namespace SteamDatabaseBackend
{
    class DepotProcessor
    {
        private sealed class DepotFile
        {
            public string Hash = "0000000000000000000000000000000000000000";
            public string Name;
            public ulong Size;
            public uint Flags;
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
                "content1.steampowered.com", // Limelight
                "content2.steampowered.com", // Level3
                "content3.steampowered.com", // Highwinds
                //"content4.steampowered.com", // EdgeCast, seems to be missing content
                "content5.steampowered.com", // CloudFront
                //"content6.steampowered.com", // Comcast, non optimal
                "content7.steampowered.com", // Akamai
                "content8.steampowered.com" // Akamai
            };

            manager.Register(new Callback<SteamApps.CDNAuthTokenCallback>(OnCDNAuthTokenCallback));
            manager.Register(new Callback<SteamApps.DepotKeyCallback>(OnDepotKeyCallback));
        }

        public void Process(uint appID, uint changeNumber, KeyValue depots)
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
                    // If there is no public manifest for this depot, it still could have some sort of open beta

                    var branch = depot["manifests"].Children.SingleOrDefault(x => x.Name != "local");

                    if (branch == null || !ulong.TryParse(branch.Value, out manifestID))
                    {
                        DbWorker.ExecuteNonQuery("INSERT INTO `Depots` (`DepotID`, `Name`) VALUES (@DepotID, @Name) ON DUPLICATE KEY UPDATE `LastUpdated` = CURRENT_TIMESTAMP(), `Name` = @Name",
                            new MySqlParameter("@DepotID", depotID),
                            new MySqlParameter("@Name", depotName)
                        );

                        continue;
                    }

                    Log.WriteInfo("Depot Processor", "Failed to find public branch for depot {0} (parent {1}) - but found another branch: {2} (manifest: {3})", depotID, appID, branch.Name, branch.AsString());
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
                string currentDepotName = string.Empty;

                // Check if manifestid in our database is equal
                using (var reader = DbWorker.ExecuteReader("SELECT `Name`, `ManifestID`, `BuildID` FROM `Depots` WHERE `DepotID` = @DepotID LIMIT 1", new MySqlParameter("DepotID", depotID)))
                {
                    if (reader.Read())
                    {
                        currentBuildID = reader.GetInt32("buildID");
                        currentDepotName = reader.GetString("Name");

                        request.PreviousManifestID = reader.GetUInt64("ManifestID");

                        if (request.PreviousManifestID == manifestID && Settings.Current.FullRun < 2)
                        {
                            // Update depot name if changed
                            if(!depotName.Equals(currentDepotName))
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
                if (currentBuildID != buildID || request.PreviousManifestID != request.ManifestID || !depotName.Equals(currentDepotName))
                {
                    DbWorker.ExecuteNonQuery("INSERT INTO `Depots` (`DepotID`, `Name`, `BuildID`, `ManifestID`) VALUES (@DepotID, @Name, @BuildID, @ManifestID) ON DUPLICATE KEY UPDATE `LastUpdated` = CURRENT_TIMESTAMP(), `Name` = @Name, `BuildID` = @BuildID, `ManifestID` = @ManifestID",
                        new MySqlParameter("@DepotID", request.DepotID),
                        new MySqlParameter("@BuildID", buildID),
                        new MySqlParameter("@ManifestID", request.ManifestID),
                        new MySqlParameter("@Name", request.DepotName)
                    );

                    if (request.PreviousManifestID != request.ManifestID)
                    {
                        MakeHistory(request, string.Empty, "manifest_change", request.PreviousManifestID, request.ManifestID);
                    }
                }

                request.Server = CDNServers[new Random().Next(CDNServers.Count)];

                JobManager.AddJob(() => Steam.Instance.Apps.GetCDNAuthToken(depotID, request.Server), request);
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
                    Log.WriteError("Depot Processor", "Failed to get CDN auth token for depot {0} (parent {1}) - {2}", request.DepotID, request.ParentAppID, callback.Result);
                }

                RemoveLock(request.DepotID);

                return;
            }

            request.CDNToken = callback.Token;

            JobManager.AddJob(() => Steam.Instance.Apps.GetDepotDecryptionKey(request.DepotID, request.ParentAppID), request);
        }

        private void OnDepotKeyCallback(SteamApps.DepotKeyCallback callback)
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
                    Log.WriteError("Depot Processor", "Failed to get depot key for depot {0} (parent {1}) - {2}", callback.DepotID, request.ParentAppID, callback.Result);
                }

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
                Application.ProcessorPool.QueueWorkItem(TryDownloadManifest, request, WorkItemPriority.Lowest);
            }
            else
            {
                Application.SecondaryPool.QueueWorkItem(TryDownloadManifest, request);
            }
        }

        private void TryDownloadManifest(ManifestJob request)
        {
            try
            {
                DownloadManifest(request);
            }
            catch (Exception e)
            {
                Log.WriteError("Depot Processor", "Caught exception while processing depot {0}: {1}\n{2}", request.DepotID, e.Message, e.StackTrace);
            }

            RemoveLock(request.DepotID);

            Log.WriteDebug("Depot Processor", "Processed depot {0} ({1} depot locks left)", request.DepotID, DepotLocks.Count);
        }

        private void DownloadManifest(ManifestJob request)
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

                if (Application.ImportantApps.ContainsKey(request.ParentAppID))
                {
                    IRC.Instance.AnnounceImportantAppUpdate(request.ParentAppID, "Important depot update: {0}{1}{2} -{3} failed to download depot manifest", Colors.BLUE, request.DepotName, Colors.NORMAL, Colors.RED);
                }

                return;
            }

            if (Application.ImportantApps.ContainsKey(request.ParentAppID))
            {
                IRC.Instance.AnnounceImportantAppUpdate(request.ParentAppID, "Important depot update: {0}{1}{2} -{3} {4}", Colors.BLUE, request.DepotName, Colors.NORMAL, Colors.DARKBLUE, SteamDB.GetDepotURL(request.DepotID, "history"));
            }

            if (FileDownloader.IsImportantDepot(request.DepotID))
            {
                Application.SecondaryPool.QueueWorkItem(FileDownloader.DownloadFilesFromDepot, request, depotManifest, WorkItemPriority.BelowNormal);
            }

            var filesNew = new List<DepotFile>();
            var filesOld = new Dictionary<string, DepotFile>();

            using (var reader = DbWorker.ExecuteReader("SELECT `File`, `Hash`, `Size`, `Flags` FROM `DepotsFiles` WHERE `DepotID` = @DepotID", new MySqlParameter("DepotID", request.DepotID)))
            {
                while (reader.Read())
                {
                    var fileName = reader.GetString("File");

                    if (filesOld.ContainsKey(fileName))
                    {
                        Log.WriteError("Depot Processor", "Skipping file {0} in depot {1} (from parent {2}) because we already got one", fileName, request.DepotID, request.ParentAppID);

                        continue;
                    }

                    filesOld.Add(fileName, new DepotFile
                    {
                        Name = fileName,
                        Hash = reader.GetString("Hash"),
                        Size = reader.GetUInt64("Size"),
                        Flags = reader.GetUInt32("Flags")
                    });
                }
            }

            foreach (var file in depotManifest.Files)
            {
                var depotFile = new DepotFile
                {
                    Name = file.FileName.Replace('\\', '/'),
                    Size = file.TotalSize,
                    Flags = (uint)file.Flags
                };

                if (file.FileHash.Length > 0 && !file.Flags.HasFlag(EDepotFileFlag.Directory))
                {
                    depotFile.Hash = string.Concat(Array.ConvertAll(file.FileHash, x => x.ToString("X2")));
                }

                filesNew.Add(depotFile);
            }

            bool shouldHistorize = filesOld.Count > 0; // Don't historize file additions if we didn't have any data before
            var filesAdded = new List<DepotFile>();

            foreach (var file in filesNew)
            {
                if (filesOld.ContainsKey(file.Name))
                {
                    var oldFile = filesOld[file.Name];
                    var updateFile = false;

                    if (oldFile.Size != file.Size || !file.Hash.Equals(oldFile.Hash))
                    {
                        MakeHistory(request, file.Name, "modified", oldFile.Size, file.Size);

                        updateFile = true;
                    }

                    if (oldFile.Flags != file.Flags)
                    {
                        MakeHistory(request, file.Name, "modified_flags", oldFile.Flags, file.Flags);

                        updateFile = true;
                    }

                    if (updateFile)
                    {
                        DbWorker.ExecuteNonQuery("UPDATE `DepotsFiles` SET `Hash` = @Hash, `Size` = @Size, `Flags` = @Flags WHERE `DepotID` = @DepotID AND `File` = @File",
                            new MySqlParameter("@DepotID", request.DepotID),
                            new MySqlParameter("@File", file.Name),
                            new MySqlParameter("@Hash", file.Hash),
                            new MySqlParameter("@Size", file.Size),
                            new MySqlParameter("@Flags", file.Flags)
                        );
                    }

                    filesOld.Remove(file.Name);
                }
                else
                {
                    // We want to historize modifications first, and only then deletions and additions
                    filesAdded.Add(file);
                }
            }

            foreach (var file in filesOld)
            {
                MakeHistory(request, file.Value.Name, "removed");

                DbWorker.ExecuteNonQuery("DELETE FROM `DepotsFiles` WHERE `DepotID` = @DepotID AND `File` = @File",
                    new MySqlParameter("@DepotID", request.DepotID),
                    new MySqlParameter("@File", file.Value.Name)
                );
            }

            foreach (var file in filesAdded)
            {
                if (shouldHistorize)
                {
                    MakeHistory(request, file.Name, "added");
                }

                DbWorker.ExecuteNonQuery("INSERT INTO `DepotsFiles` (`DepotID`, `File`, `Hash`, `Size`, `Flags`) VALUES (@DepotID, @File, @Hash, @Size, @Flags)",
                    new MySqlParameter("@DepotID", request.DepotID),
                    new MySqlParameter("@File", file.Name),
                    new MySqlParameter("@Hash", file.Hash),
                    new MySqlParameter("@Size", file.Size),
                    new MySqlParameter("@Flags", file.Flags)
                );
            }
        }

        private static void MakeHistory(ManifestJob request, string file, string action, ulong oldValue = 0, ulong newValue = 0)
        {
            DbWorker.ExecuteNonQuery(
                "INSERT INTO `DepotsHistory` (`ChangeID`, `DepotID`, `File`, `Action`, `OldValue`, `NewValue`) VALUES (@ChangeID, @DepotID, @File, @Action, @OldValue, @NewValue)",
                new MySqlParameter("@DepotID", request.DepotID),
                new MySqlParameter("@ChangeID", request.ChangeNumber),
                new MySqlParameter("@File", file),
                new MySqlParameter("@Action", action),
                new MySqlParameter("@OldValue", oldValue),
                new MySqlParameter("@NewValue", newValue)
            );
        }

        private void RemoveLock(uint depotID)
        {
            byte microsoftWhyIsThereNoRemoveMethodWithoutSecondParam;

            DepotLocks.TryRemove(depotID, out microsoftWhyIsThereNoRemoveMethodWithoutSecondParam);
        }
    }
}
