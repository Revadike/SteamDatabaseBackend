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
using SteamKit2;

namespace SteamDatabaseBackend
{
    static class FileDownloader
    {
        private static readonly Dictionary<uint, List<string>> ImportantDepots = new Dictionary<uint, List<string>>
        {
            // Team Fortress 2
            {
                232251,
                new List<string>
                {
                    //"tf/bin/client.dll",
                    "tf/bin/server.dll"
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
                573,
                new List<string>
                {
                    "bin/engine.dll",
                    //"dota/bin/client.dll",
                    "dota/bin/server.dll"
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
                205793,
                new List<string>
                {
                    "bin/engine.dll",
                    //"dota/bin/client.dll",
                    "dota/bin/server.dll"
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
                732,
                new List<string>
                {
                    "bin/engine.dll",
                    //"csgo/bin/client.dll",
                    "csgo/bin/server.dll"
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

                using (var fs = File.Open(downloadPath, FileMode.OpenOrCreate))
                {
                    fs.SetLength((long)file.TotalSize);

                    // TODO: We *could* verify each chunk and only download needed ones
                    foreach (var chunk in file.Chunks)
                    {
                        var downloaded = false;

                        // Who needs paralleled tasks anyway
                        for (var i = 0; i <= 5; i++)
                        {
                            try
                            {
                                var chunkData = CDNClient.DownloadDepotChunk(job.DepotID, chunk, job.Server, job.CDNToken, job.DepotKey);

                                fs.Seek((long)chunk.Offset, SeekOrigin.Begin);
                                fs.Write(chunkData.Data, 0, chunkData.Data.Length);

                                Log.WriteDebug("FileDownloader", "Downloaded {0} ({1}/{2})", file.FileName, ++count, file.Chunks.Count);

                                downloaded = true;

                                break;
                            }
                            catch (Exception e)
                            {
                                Log.WriteError("FileDownloader", "Error downloading {0}: {1}", file.FileName, e.Message);
                            }
                        }

                        if (!downloaded)
                        {
                            break;
                        }
                    }
                }

                if (count == file.Chunks.Count)
                {
                    IRC.Instance.SendOps("{0}[{1}]{2} Downloaded {3}{4}", Colors.OLIVE, Steam.GetAppName(job.ParentAppID), Colors.NORMAL, Colors.OLIVE, file.FileName);

                    Log.WriteInfo("FileDownloader", "Downloaded {0} from {1}", file.FileName, Steam.GetAppName(job.ParentAppID));

                    // TODO: Verify hash

                    var finalPath = Path.Combine(directory, Path.GetFileName(file.FileName));

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

                    Log.WriteError("FileDownloader", "Failed to download {0}: Only {0} out of {1} chunks downloaded", downloadPath, count, file.Chunks.Count);

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
