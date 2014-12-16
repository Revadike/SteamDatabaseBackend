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
using System.Net;
using System.Security.Cryptography;
using System.Threading.Tasks;
using SteamKit2;

namespace SteamDatabaseBackend
{
    static class FileDownloader
    {
        private const string FILES_DIRECTORY = "files";

        private static readonly Dictionary<uint, List<string>> ImportantDepots = new Dictionary<uint, List<string>>
        {
            // Team Fortress 2
            {
                232252,
                new List<string>
                {
                    "tf/bin/client.dylib",
                    "tf/bin/server.dylib"
                }
            },
            {
                441,
                new List<string>
                {
                    "tf/steam.inf",
                    "tf/resource/tf_english.txt",
                    "tf/tf2_misc_dir.vpk",
                    "tf/tf2_sound_misc_dir.vpk",
                    "tf/tf2_sound_vo_english_dir.vpk",
                    "tf/tf2_textures_dir.vpk"
                }
            },
            // Dota 2
            {
                574,
                new List<string>
                {
                    "bin/engine.dylib",
                    "dota/bin/client.dylib",
                    "dota/bin/server.dylib"
                }
            },
            {
                571,
                new List<string>
                {
                    "dota/steam.inf",
                    "dota/resource/dota_english.txt",
                    "dota/pak01_dir.vpk"
                }
            },
            // Dota 2 Workshop
            {
                313250,
                new List<string>
                {
                    "dota_ugc/game/bin/win64/engine2.dll",
                    "dota_ugc/game/bin/win64/vphysics2.dll",
                    "dota_ugc/game/dota/bin/win64/client.dll",
                    "dota_ugc/game/dota/bin/win64/server.dll"
                }
            },
            // Dota 2 Test
            {
                205794,
                new List<string>
                {
                    "bin/engine.dylib",
                    "dota/bin/client.dylib",
                    "dota/bin/server.dylib"
                }
            },
            {
                205791,
                new List<string>
                {
                    "dota/steam.inf",
                    "dota/resource/dota_english.txt",
                    "dota/pak01_dir.vpk"
                }
            },
            // Counter-Strike: Global Offensive
            {
                733,
                new List<string>
                {
                    "bin/engine.dylib",
                    "csgo/bin/client.dylib",
                    "csgo/bin/server.dylib"
                }
            },
            {
                731,
                new List<string>
                {
                    "csgo/steam.inf",
                    "csgo/resource/csgo_english.txt",
                    "csgo/pak01_dir.vpk"
                }
            },
            // Half-Life 2
            {
                221,
                new List<string>
                {
                    "hl2/steam.inf",
                    "hl2/resource/hl2_english.txt",
                    "hl2/hl2_misc_dir.vpk",
                    "hl2/hl2_pak_dir.vpk",
                    "hl2/hl2_sound_misc_dir.vpk",
                    "hl2/hl2_sound_vo_english_dir.vpk",
                    "hl2/hl2_textures_dir.vpk"
                }
            },
            {
                223,
                new List<string>
                {
                    "hl2/bin/client.dylib",
                    "hl2/bin/server.dylib"
                }
            },
            // Half-Life 2: Episode One
            {
                389,
                new List<string>
                {
                    "episodic/ep1_pak_dir.vpk"
                }
            },
            // Half-Life 2: Episode Two
            {
                420,
                new List<string>
                {
                    "ep2/ep2_pak_dir.vpk"
                }
            },
            // Half-Life 2: Deathmatch
            {
                321,
                new List<string>
                {
                    "hl2mp/steam.inf",
                    "hl2mp/hl2mp_pak_dir.vpk"
                }
            },
            {
                232372,
                new List<string>
                {
                    "hl2mp/bin/client.dylib",
                    "hl2mp/bin/server.dylib"
                }
            },
            // Portal
            {
                401,
                new List<string>
                {
                    "portal/steam.inf",
                    "portal/resource/portal_english.txt",
                    "portal/portal_pak_dir.vpk"
                }
            },
            {
                403,
                new List<string>
                {
                    "portal/bin/client.dylib",
                    "portal/bin/server.dylib"
                }
            },
            // Portal 2
            {
                621,
                new List<string>
                {
                    "portal2/steam.inf",
                    "portal2/pak01_dir.vpk",
                    "portal2/resource/portal2_english.txt"
                }
            },
            {
                624,
                new List<string>
                {
                    "portal2/bin/client.dylib",
                    "portal2/bin/server.dylib"
                }
            },
            // Alien Swarm
            {
                631,
                new List<string>
                {
                    "swarm/bin/client.dll",
                    "swarm/bin/server.dll",

                    "swarm/steam.inf",
                    "swarm/pak01_dir.vpk",
                    "swarm/resource/swarm_english.txt"
                }
            },
            // Left 4 Dead
            {
                502,
                new List<string>
                {
                    "left4dead/steam.inf",
                    "left4dead/pak01_dir.vpk"
                }
            },
            {
                515,
                new List<string>
                {
                    "left4dead/bin/client.dylib",
                    "left4dead/bin/server.dylib"
                }
            },
            // Left 4 Dead 2
            {
                551,
                new List<string>
                {
                    "left4dead2/steam.inf",
                    "left4dead2/pak01_dir.vpk"
                }
            },
            {
                553,
                new List<string>
                {
                    "left4dead2/bin/client.dylib",
                    "left4dead2/bin/server.dylib"
                }
            },
            // OpenVR
            {
                250822,
                new List<string>
                {
                    "bin/openvr.dylib",
                    "bin/vrclient.dylib"
                }
            },
        };

        private static CDNClient CDNClient;

        public static void SetCDNClient(CDNClient cdnClient)
        {
            CDNClient = cdnClient;

            try
            {
                string filesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FILES_DIRECTORY);
                Directory.CreateDirectory(filesDir);
            }
            catch (Exception ex)
            {
                Log.WriteError("FileDownloader", "Unable to create files directory: {0}", ex.Message);
            }
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
                string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FILES_DIRECTORY, job.DepotID.ToString());
                string finalPath = Path.Combine(directory, Path.GetFileName(file.FileName));

                if (File.Exists(finalPath))
                {
                    using (var fs = File.Open(finalPath, FileMode.Open))
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
                else if (File.Exists(downloadPath))
                {
                    Log.WriteWarn("FileDownloader", "Removing stale {0}", file.FileName);

                    File.Delete(downloadPath);
                }

                Log.WriteInfo("FileDownloader", "Downloading {0} ({1} bytes, {2} chunks)", file.FileName, file.TotalSize, file.Chunks.Count);

                uint count = 0;
                byte[] checksum;
                string lastError = "or checksum failed";

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
                            catch (WebException e)
                            {
                                lastError = e.Message;
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
                    IRC.Instance.SendOps("{0}[{1}]{2} Failed to download {3}: Only {4} out of {5} chunks downloaded ({6})", Colors.OLIVE, Steam.GetAppName(job.ParentAppID), Colors.NORMAL, file.FileName, count, file.Chunks.Count, lastError);


                    Log.WriteError("FileDownloader", "Failed to download {0}: Only {1} out of {2} chunks downloaded ({3})", file.FileName, count, file.Chunks.Count, lastError);

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
