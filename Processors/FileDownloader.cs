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
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SteamKit2;

namespace SteamDatabaseBackend
{
    static class FileDownloader
    {
        private static readonly object updateLock = new object();
        private static Dictionary<uint, Regex> Files = new Dictionary<uint, Regex>();
        private static CDNClient CDNClient;

        public static void SetCDNClient(CDNClient cdnClient)
        {
            CDNClient = cdnClient;

            ReloadFileList();

            string filesDir = Path.Combine(Application.Path, "files", ".support", "hashes");
            Directory.CreateDirectory(filesDir);
        }

        public static void ReloadFileList()
        {
            string file = Path.Combine(Application.Path, "files", "files.json");

            if (!File.Exists(file))
            {
                Log.WriteWarn("FileDownloader", "files/files.json not found. No files will be downloaded.");
            }
            else
            {
                Files = new Dictionary<uint, Regex>();

                var files = JsonConvert.DeserializeObject<Dictionary<uint, List<string>>>(File.ReadAllText(file), new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error });

                foreach (var depot in files)
                {
                    string pattern = string.Format("^({0})$", string.Join("|", depot.Value.Select(x => ConvertFileMatch(x))));

                    Files[depot.Key] = new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);
                }
            }
        }

        public static bool IsImportantDepot(uint depotID)
        {
            return Files.ContainsKey(depotID);
        }

        /*
         * Here be dragons.
         */
        public static void DownloadFilesFromDepot(DepotProcessor.ManifestJob job, DepotManifest depotManifest)
        {
            var randomGenerator = new Random();
            var files = depotManifest.Files.Where(x => IsFileNameMatching(job.DepotID, x.FileName)).ToList();
            var filesUpdated = false;

            var hashesFile = Path.Combine(Application.Path, "files", ".support", "hashes", string.Format("{0}.json", job.DepotID));
            var hashes = new Dictionary<string, byte[]>();

            if (File.Exists(hashesFile))
            {
                hashes = JsonConvert.DeserializeObject<Dictionary<string, byte[]>>(File.ReadAllText(hashesFile));
            }

            Log.WriteDebug("FileDownloader", "Will download {0} files from depot {1}", files.Count, job.DepotID);

            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = 2 }, (file, state2) =>
            {
                string directory    = Path.Combine(Application.Path, "files", job.DepotID.ToString(), Path.GetDirectoryName(file.FileName));
                string finalPath    = Path.Combine(directory, Path.GetFileName(file.FileName));
                string downloadPath = Path.Combine(Path.GetTempPath(), Path.ChangeExtension(Path.GetRandomFileName(), ".steamdb_tmp"));

                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                else if (file.TotalSize == 0)
                {
                    if (File.Exists(finalPath))
                    {
                        var f = new FileInfo(finalPath);

                        if(f.Length == 0)
                        {
#if DEBUG
                            Log.WriteDebug("FileDownloader", "{0} is already empty", file.FileName);
#endif

                            return;
                        }
                    }
                    else
                    {
                        File.Create(finalPath);

                        Log.WriteInfo("FileDownloader", "{0} created an empty file", file.FileName);

                        return;
                    }
                }
                else if(hashes.ContainsKey(file.FileName) && file.FileHash.SequenceEqual(hashes[file.FileName]))
                {
#if DEBUG
                    Log.WriteDebug("FileDownloader", "{0} already matches the file we have", file.FileName);
#endif

                    return;
                }

                var chunks = file.Chunks.OrderBy(x => x.Offset).ToList();

                Log.WriteInfo("FileDownloader", "Downloading {0} ({1} bytes, {2} chunks)", file.FileName, file.TotalSize, chunks.Count);

                uint count = 0;
                byte[] checksum;
                string lastError = "or checksum failed";
                string oldChunksFile;

                using (var sha = new SHA1Managed())
                {
                    oldChunksFile = Path.Combine(Application.Path, "files", ".support", "chunks",
                        string.Format("{0}-{1}.json", job.DepotID, BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(file.FileName))))
                    );
                }

                using (var fs = File.Open(downloadPath, FileMode.OpenOrCreate, FileAccess.ReadWrite))
                {
                    fs.SetLength((long)file.TotalSize);

                    var lockObject = new object();
                    var neededChunks = new List<DepotManifest.ChunkData>();

                    if (File.Exists(oldChunksFile) && File.Exists(finalPath))
                    {
                        var oldChunks = JsonConvert.DeserializeObject<List<DepotManifest.ChunkData>>(
                                            File.ReadAllText(oldChunksFile),
                                            new JsonSerializerSettings { PreserveReferencesHandling = PreserveReferencesHandling.All }
                                        );

                        using (var fsOld = File.Open(finalPath, FileMode.Open, FileAccess.Read))
                        {
                            foreach (var chunk in chunks)
                            {
                                var oldChunk = oldChunks.FirstOrDefault(c => c.ChunkID.SequenceEqual(chunk.ChunkID));

                                if (oldChunk != null)
                                {
                                    var oldData = new byte[oldChunk.UncompressedLength];
                                    fsOld.Seek((long)oldChunk.Offset, SeekOrigin.Begin);
                                    fsOld.Read(oldData, 0, oldData.Length);

                                    var existingChecksum = Utils.AdlerHash(oldData);

                                    if (existingChecksum.SequenceEqual(chunk.Checksum))
                                    {
                                        fs.Seek((long)chunk.Offset, SeekOrigin.Begin);
                                        fs.Write(oldData, 0, oldData.Length);

#if DEBUG
                                        Log.WriteDebug("FileDownloader", "{0} Found chunk ({1}), not downloading ({2}/{3})", file.FileName, chunk.Offset, ++count, chunks.Count);
#endif
                                    }
                                    else
                                    {
                                        neededChunks.Add(chunk);

#if DEBUG
                                        Log.WriteDebug("FileDownloader", "{0} Found chunk ({1}), but checksum differs", file.FileName, chunk.Offset);
#endif
                                    }
                                }
                                else
                                {
                                    neededChunks.Add(chunk);
                                }
                            }
                        }
                    }
                    else
                    {
                        neededChunks = chunks;
                    }

                    Parallel.ForEach(neededChunks, new ParallelOptions { MaxDegreeOfParallelism = 3 }, (chunk, state) =>
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

                                    Log.WriteDebug("FileDownloader", "Downloaded {0} ({1}/{2})", file.FileName, ++count, chunks.Count);
                                }

                                downloaded = true;

                                break;
                            }
                            catch (Exception e)
                            {
                                lastError = e.Message;
                            }

                            // See https://developers.google.com/drive/web/handle-errors
                            Task.Delay((1 << i) * 1000 + randomGenerator.Next(1001)).Wait();
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
                    Log.WriteInfo("FileDownloader", "Downloaded {0} from {1}", file.FileName, Steam.GetAppName(job.ParentAppID));

                    hashes[file.FileName] = checksum;

                    if (File.Exists(finalPath))
                    {
                        File.Delete(finalPath);
                    }

                    File.Move(downloadPath, finalPath);

                    if (chunks.Count > 1)
                    {
                        File.WriteAllText(oldChunksFile,
                            JsonConvert.SerializeObject(
                                chunks,
                                Formatting.None,
                                new JsonSerializerSettings { PreserveReferencesHandling = PreserveReferencesHandling.All }
                            )
                        );
                    }
                    else if (File.Exists(oldChunksFile))
                    {
                        File.Delete(oldChunksFile);
                    }

                    filesUpdated = true;
                }
                else
                {
                    IRC.Instance.SendOps("{0}[{1}]{2} Failed to download {3}: Only {4} out of {5} chunks downloaded ({6})",
                        Colors.OLIVE, Steam.GetAppName(job.ParentAppID), Colors.NORMAL, file.FileName, count, chunks.Count, lastError);

                    Log.WriteError("FileDownloader", "Failed to download {0}: Only {1} out of {2} chunks downloaded from {3} ({4})",
                        file.FileName, count, chunks.Count, job.Server, lastError);

                    File.Delete(downloadPath);
                }
            });

            if (filesUpdated)
            {
                File.WriteAllText(hashesFile, JsonConvert.SerializeObject(hashes));

                var updateScript = Path.Combine(Application.Path, "files", "update.sh");

                if (File.Exists(updateScript))
                {
                    lock (updateLock)
                    {
                        // YOLO
                        Process.Start(updateScript, job.DepotID.ToString());
                    }
                }
            }
        }

        private static bool IsFileNameMatching(uint depotID, string fileName)
        {
            return Files[depotID].IsMatch(fileName.Replace('\\', '/'));
        }

        private static string ConvertFileMatch(string input)
        {
            if (input.StartsWith("regex:", StringComparison.Ordinal))
            {
                return input.Substring(6);
            }

            return Regex.Escape(input);
        }
    }
}
