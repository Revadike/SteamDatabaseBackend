/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Newtonsoft.Json;
using SteamKit2;

namespace SteamDatabaseBackend
{
    static class FileDownloader
    {
        public static JsonSerializerSettings JsonHandleAllReferences = new JsonSerializerSettings { PreserveReferencesHandling = PreserveReferencesHandling.All };
        public static JsonSerializerSettings JsonErrorMissing = new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error };

        private static Dictionary<uint, string> DownloadFolders;
        private static Dictionary<uint, Regex> Files;
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
            Files = new Dictionary<uint, Regex>();
            DownloadFolders = new Dictionary<uint, string>();

            string file = Path.Combine(Application.Path, "files", "depots_mapping.json");

            if (!File.Exists(file))
            {
                Log.WriteWarn("FileDownloader", "files/depots_mapping.json not found.");

                return;
            }

            DownloadFolders = JsonConvert.DeserializeObject<Dictionary<uint, string>>(File.ReadAllText(file), JsonErrorMissing);

            file = Path.Combine(Application.Path, "files", "files.json");

            if (!File.Exists(file))
            {
                Log.WriteWarn("FileDownloader", "files/files.json not found. No files will be downloaded.");

                return;
            }

            var files = JsonConvert.DeserializeObject<Dictionary<uint, List<string>>>(File.ReadAllText(file), JsonErrorMissing);

            foreach (var depot in files)
            {
                string pattern = string.Format("^({0})$", string.Join("|", depot.Value.Select(x => ConvertFileMatch(x))));

                Files[depot.Key] = new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);

                if (!DownloadFolders.ContainsKey(depot.Key))
                {
                    throw new InvalidDataException(string.Format("Missing depot mapping for depotid {0}.", depot.Key));
                }
            }
        }

        public static string GetAppDownloadFolder(uint appID)
        {
            return DownloadFolders.ContainsKey(appID) ? DownloadFolders[appID] : null;
        }

        public static bool IsImportantDepot(uint depotID)
        {
            return Files.ContainsKey(depotID);
        }

        /*
         * Here be dragons.
         */
        public static EResult DownloadFilesFromDepot(uint appID, DepotProcessor.ManifestJob job, DepotManifest depotManifest)
        {
            var files = depotManifest.Files.Where(x => IsFileNameMatching(job.DepotID, x.FileName)).ToList();
            var downloadState = EResult.Fail;
            
            var hashesFile = Path.Combine(Application.Path, "files", ".support", "hashes", string.Format("{0}.json", job.DepotID));
            Dictionary<string, byte[]> hashes;

            if (File.Exists(hashesFile))
            {
                hashes = JsonConvert.DeserializeObject<Dictionary<string, byte[]>>(File.ReadAllText(hashesFile));
            }
            else
            {
                hashes = new Dictionary<string, byte[]>();
            }

            Log.WriteDebug("FileDownloader", "Will download {0} files from depot {1}", files.Count, job.DepotID);

            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = 2 }, async (file, state) =>
            {
                hashes.TryGetValue(file.FileName, out var hash);

                var fileState = await DownloadFile(appID, job, file, hash);

                if (fileState == EResult.OK)
                {
                    hashes[file.FileName] = file.FileHash;
                }

                if (downloadState == EResult.DataCorruption)
                {
                    return;
                }

                if (fileState == EResult.OK || fileState == EResult.DataCorruption)
                {
                    downloadState = fileState;
                }
            });

            if (downloadState == EResult.OK)
            {
                File.WriteAllText(hashesFile, JsonConvert.SerializeObject(hashes));

                job.Result = EResult.OK;
            }
            else
            {
                job.Result = EResult.Ignored;
            }

            return job.Result;
        }

        private static async Task<EResult> DownloadFile(uint appID, DepotProcessor.ManifestJob job, DepotManifest.FileData file, byte[] hash)
        {
            var directory = Path.Combine(Application.Path, "files", DownloadFolders[job.DepotID], Path.GetDirectoryName(file.FileName));
            var finalPath = new FileInfo(Path.Combine(directory, Path.GetFileName(file.FileName)));
            var downloadPath = new FileInfo(Path.Combine(Path.GetTempPath(), Path.ChangeExtension(Path.GetRandomFileName(), ".steamdb_tmp")));

            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            else if (file.TotalSize == 0)
            {
                if (!finalPath.Exists)
                {
                    finalPath.Create();

                    Log.WriteInfo("FileDownloader", "{0} created an empty file", file.FileName);

                    return EResult.SameAsPreviousValue;
                }
                else if (finalPath.Length == 0)
                {
#if DEBUG
                    Log.WriteDebug("FileDownloader", "{0} is already empty", file.FileName);
#endif

                    return EResult.SameAsPreviousValue;
                }
            }
            else if (hash != null && file.FileHash.SequenceEqual(hash))
            {
#if DEBUG
                Log.WriteDebug("FileDownloader", "{0} already matches the file we have", file.FileName);
#endif

                return EResult.SameAsPreviousValue;
            }

            var chunks = file.Chunks.OrderBy(x => x.Offset).ToList();

            Log.WriteInfo("FileDownloader", "Downloading {0} ({1} bytes, {2} chunks)", file.FileName, file.TotalSize, chunks.Count);
            
            byte[] checksum;
            string oldChunksFile;
            var neededChunks = new List<DepotManifest.ChunkData>();

            using (var sha = SHA1.Create())
            {
                oldChunksFile = Path.Combine(Application.Path, "files", ".support", "chunks",
                    string.Format("{0}-{1}.json", job.DepotID, BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(file.FileName))))
                );
            }

            using (var fs = downloadPath.Open(FileMode.OpenOrCreate, FileAccess.ReadWrite))
            {
                fs.SetLength((long)file.TotalSize);

                if (finalPath.Exists && File.Exists(oldChunksFile))
                {
                    var oldChunks = JsonConvert.DeserializeObject<List<DepotManifest.ChunkData>>(File.ReadAllText(oldChunksFile), JsonHandleAllReferences);

                    using (var fsOld = finalPath.Open(FileMode.Open, FileAccess.Read))
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
                                    Log.WriteDebug("FileDownloader", "{0} Found chunk ({1}), not downloading", file.FileName, chunk.Offset);
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
            }

            var count = neededChunks.Count;
            var chunkCancellation = new CancellationTokenSource();
            var chunkSemaphore = new SemaphoreSlim(1);
            var chunkTasks = new Task[count];

            for (var i = 0; i < chunkTasks.Length; i++)
            {
                var chunk = neededChunks[i];
                chunkTasks[i] = TaskManager.Run(async () =>
                {
                    chunkCancellation.Token.ThrowIfCancellationRequested();

                    try
                    {
                        await chunkSemaphore.WaitAsync().ConfigureAwait(false);

                        var result = await DownloadChunk(job, chunk, downloadPath);

                        if (!result)
                        {
                            Log.WriteWarn("FileDownloader", "Failed to download {0} ({1}/{2})", file.FileName, count, chunks.Count);

                            chunkCancellation.Cancel();
                        }
                        else
                        {
                            Log.WriteDebug("FileDownloader", "Downloaded {0} ({1}/{2})", file.FileName, ++count, chunks.Count);
                        }
                    }
                    finally
                    {
                        chunkSemaphore.Release();
                    }
                }).Unwrap();
            }

            await Task.WhenAll(chunkTasks).ConfigureAwait(false);

            using (var fs = downloadPath.Open(FileMode.Open, FileAccess.ReadWrite))
            {
                fs.Seek(0, SeekOrigin.Begin);

                using (var sha = SHA1.Create())
                {
                    checksum = sha.ComputeHash(fs);
                }
            }

            if (!file.FileHash.SequenceEqual(checksum))
            {
                IRC.Instance.SendOps("{0}[{1}]{2} Failed to correctly download {3}{4}",
                    Colors.OLIVE, Steam.GetAppName(appID), Colors.NORMAL, Colors.BLUE, file.FileName);

                Log.WriteWarn("FileDownloader", "Failed to download {0}: Only {1} out of {2} chunks downloaded from {3}",
                    file.FileName, count, chunks.Count, job.Server);

                downloadPath.Delete();

                return EResult.DataCorruption;
            }

            Log.WriteInfo("FileDownloader", "Downloaded {0} from {1}", file.FileName, Steam.GetAppName(appID));
            
            finalPath.Delete();

            downloadPath.MoveTo(finalPath.FullName);
            
            if (chunks.Count > 1)
            {
                File.WriteAllText(oldChunksFile, JsonConvert.SerializeObject(chunks, Formatting.None, JsonHandleAllReferences));
            }
            else if (File.Exists(oldChunksFile))
            {
                File.Delete(oldChunksFile);
            }

            return EResult.OK;
        }

        private static async Task<bool> DownloadChunk(DepotProcessor.ManifestJob job, DepotManifest.ChunkData chunk, FileInfo downloadPath)
        {
            for (var i = 0; i <= 5; i++)
            {
                try
                {
                    var chunkData = await CDNClient.DownloadDepotChunkAsync(job.DepotID, chunk, job.Server, job.CDNToken, job.DepotKey);

                    using (var fs = downloadPath.Open(FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                    {
                        fs.Seek((long)chunk.Offset, SeekOrigin.Begin);
                        await fs.WriteAsync(chunkData.Data, 0, chunkData.Data.Length);
                    }

                    return true;
                }
                catch (Exception e)
                {
                    Log.WriteWarn("FileDownloader", "{0} exception: {1}", job.DepotID, e.Message);
                }

                await Task.Delay(Utils.ExponentionalBackoff(i));
            }

            return false;
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
