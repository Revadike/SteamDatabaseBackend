/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using SteamKit2;

namespace SteamDatabaseBackend
{
    static class FileDownloader
    {
        private static readonly Dictionary<uint, List<string>> ImportantDepots = new Dictionary<uint, List<string>>
        {
            // Team Fortress 2
            {
                232252,
                new List<string>
                {
                    //"tf/bin/client.dylib",
                    "tf/bin/server.dylib"
                }
            },
            {
                441,
                new List<string>
                {
                    "tf/steam.inf",
                    "tf/resource/tf_english.txt"
                }
            },
            // Dota 2
            {
                574,
                new List<string>
                {
                    "bin/engine.dylib",
                    //"dota/bin/client.dylib",
                    "dota/bin/server.dylib"
                }
            },
            {
                571,
                new List<string>
                {
                    "dota/steam.inf",
                    "dota/resource/dota_english.txt"
                }
            },
            // Dota 2 Workshop
            {
                313250,
                new List<string>
                {
                    "dota_ugc/game/dota/bin/win64/server.dll",
                    "dota_ugc/game/bin/win64/engine2.dll"
                }
            },
            // Dota 2 Test
            {
                205794,
                new List<string>
                {
                    "bin/engine.dylib",
                    //"dota/bin/client.dylib",
                    "dota/bin/server.dylib"
                }
            },
            {
                205791,
                new List<string>
                {
                    "dota/steam.inf",
                    "dota/resource/dota_english.txt"
                }
            },
            // Counter-Strike: Global Offensive
            {
                733,
                new List<string>
                {
                    "bin/engine.dylib",
                    //"csgo/bin/client.dylib",
                    "csgo/bin/server.dylib"
                }
            },
            {
                731,
                new List<string>
                {
                    "csgo/steam.inf",
                    "csgo/resource/csgo_english.txt"
                }
            },
        };

        private static CDNClient CDNClient;

        public static void SetCDNClient(CDNClient cdnClient)
        {
            CDNClient = cdnClient;
        }

        public static bool IsImportantDepot(uint depotID)
        {
            return ImportantDepots.ContainsKey(depotID);
        }

        public static void DownloadFilesFromDepot(DepotProcessor.ManifestJob job, DepotManifest depotManifest)
        {
            var files = depotManifest.Files.Where(x => ImportantDepots[job.DepotID].Contains(x.FileName.Replace('\\', '/'))).ToList();
            var filesUpdated = false;

            Log.WriteDebug("FileDownloader", "Will download {0} files from depot {1}", files.Count(), job.DepotID);

            foreach (var file in files)
            {
                string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "files", job.DepotID.ToString());
                string finalPath = Path.Combine(directory, Path.GetFileName(file.FileName));

                if (File.Exists(finalPath))
                {
                    using (var fs = File.Open(finalPath, FileMode.OpenOrCreate))
                    {
                        using (var sha = new SHA1Managed())
                        {
                            if (file.FileHash.SequenceEqual(sha.ComputeHash(fs)))
                            {
                                Log.WriteDebug("FileDownloader", "{0} already matches the file we have", file.FileName);

                                continue;
                            }
                        }
                    }
                }

                string downloadPath = Path.Combine(directory, string.Concat("staged_", Path.GetFileName(file.FileName)));

                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                else if(File.Exists(downloadPath))
                {
                    Log.WriteDebug("FileDownloader", "Removing stale {0}", file.FileName);

                    File.Delete(downloadPath);
                }

                Log.WriteInfo("FileDownloader", "Downloading {0} ({1} bytes, {2} chunks)", file.FileName, file.TotalSize, file.Chunks.Count);

                uint count = 0;
                byte[] checksum;

                using (var fs = File.Open(downloadPath, FileMode.OpenOrCreate))
                {
                    fs.SetLength((long)file.TotalSize);

                    var lockObject = new object();

                    // TODO: We *could* verify each chunk and only download needed ones
                    Parallel.ForEach(file.Chunks, (chunk, state) =>
                    {
                        var downloaded = false;

                        for (var i = 0; i <= 5; i++)
                        {
                            try
                            {
                                var chunkData = CDNClient.DownloadDepotChunk(job.DepotID, chunk, job.Server, job.CDNToken, job.DepotKey);

                                lock (lockObject)
                                {
                                    fs.Seek((long)chunk.Offset, SeekOrigin.Begin);
                                    fs.Write(chunkData.Data, 0, chunkData.Data.Length);

                                    Log.WriteDebug("FileDownloader", "Downloaded {0} ({1}/{2})", file.FileName, ++count, file.Chunks.Count);
                                }

                                downloaded = true;

                                break;
                            }
                            catch (Exception e)
                            {
                                Log.WriteError("FileDownloader", "Error downloading {0} ({1}): {2} (#{3})", file.FileName, job.DepotID, e.Message, i);
                            }
                        }

                        if (!downloaded)
                        {
                            state.Stop();
                        }
                    });

                    fs.Seek(0, SeekOrigin.Begin);

                    using (var sha = new SHA1Managed())
                    {
                        checksum = sha.ComputeHash(fs);
                    }
                }

                if (file.FileHash.SequenceEqual(checksum))
                {
                    IRC.Instance.SendOps("{0}[{1}]{2} Downloaded {3}{4}", Colors.OLIVE, Steam.GetAppName(job.ParentAppID), Colors.NORMAL, Colors.OLIVE, file.FileName);

                    Log.WriteInfo("FileDownloader", "Downloaded {0} from {1}", file.FileName, Steam.GetAppName(job.ParentAppID));

                    if (File.Exists(finalPath))
                    {
                        File.Delete(finalPath);
                    }

                    File.Move(downloadPath, finalPath);

                    filesUpdated = true;
                }
                else
                {
                    IRC.Instance.SendOps("{0}[ERROR]{1} Failed to download:{2} {3} ({4} of {5} chunks)", Colors.RED, Colors.NORMAL, Colors.OLIVE, file.FileName, count, file.Chunks.Count);

                    Log.WriteError("FileDownloader", "Failed to download {0}: Only {1} out of {2} chunks downloaded (or checksum failed)", downloadPath, count, file.Chunks.Count);

                    File.Delete(downloadPath);
                }
            }

            if (filesUpdated)
            {
                var updateScript = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "files", "update.sh");

                if (File.Exists(updateScript))
                {
                    // YOLO
                    Process.Start(updateScript, job.DepotID.ToString());
                }
            }
        }
    }
}
