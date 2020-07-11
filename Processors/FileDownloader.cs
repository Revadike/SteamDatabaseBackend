/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SteamKit2;

namespace SteamDatabaseBackend
{
    internal static class FileDownloader
    {
        private static readonly JsonSerializerSettings JsonHandleAllReferences = new JsonSerializerSettings { PreserveReferencesHandling = PreserveReferencesHandling.All };
        private static readonly JsonSerializerSettings JsonErrorMissing = new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error };
        private static readonly SemaphoreSlim ChunkDownloadingSemaphore = new SemaphoreSlim(10);

        private static Dictionary<uint, string> DownloadFolders;
        private static Dictionary<uint, Regex> Files;
        private static CDNClient CDNClient;

        public static void SetCDNClient(CDNClient cdnClient)
        {
            CDNClient = cdnClient;

            ReloadFileList();

            var filesDir = Path.Combine(Application.Path, "files", ".support", "hashes");
            Directory.CreateDirectory(filesDir);
        }

        public static void ReloadFileList()
        {
            Files = new Dictionary<uint, Regex>();
            DownloadFolders = new Dictionary<uint, string>();

            var file = Path.Combine(Application.Path, "files", "depots_mapping.json");

            if (!File.Exists(file))
            {
                Log.WriteWarn(nameof(FileDownloader), "files/depots_mapping.json not found.");

                return;
            }

            DownloadFolders = JsonConvert.DeserializeObject<Dictionary<uint, string>>(File.ReadAllText(file), JsonErrorMissing);

            file = Path.Combine(Application.Path, "files", "files.json");

            if (!File.Exists(file))
            {
                Log.WriteWarn(nameof(FileDownloader), "files/files.json not found. No files will be downloaded.");

                return;
            }

            var files = JsonConvert.DeserializeObject<Dictionary<uint, List<string>>>(File.ReadAllText(file), JsonErrorMissing);

            foreach (var (depotid, fileMatches) in files)
            {
                var pattern = $"^({string.Join("|", fileMatches.Select(ConvertFileMatch))})$";

                Files[depotid] = new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);

                if (!DownloadFolders.ContainsKey(depotid))
                {
                    throw new InvalidDataException($"Missing depot mapping for depotid {depotid}.");
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
        public static async Task<EResult> DownloadFilesFromDepot(DepotProcessor.ManifestJob job, DepotManifest depotManifest)
        {
            var files = depotManifest.Files.Where(x => IsFileNameMatching(job.DepotID, x.FileName)).ToList();
            var downloadState = EResult.Fail;

            var hashesFile = Path.Combine(Application.Path, "files", ".support", "hashes", $"{job.DepotID}.json");
            ConcurrentDictionary<string, byte[]> hashes;

            if (File.Exists(hashesFile))
            {
                hashes = JsonConvert.DeserializeObject<ConcurrentDictionary<string, byte[]>>(await File.ReadAllTextAsync(hashesFile));
            }
            else
            {
                hashes = new ConcurrentDictionary<string, byte[]>();
            }

            foreach (var file in hashes.Keys.Except(files.Select(x => x.FileName)))
            {
                Log.WriteWarn(nameof(FileDownloader), $"\"{file}\" no longer exists in manifest");
            }

            Log.WriteInfo($"FileDownloader {job.DepotID}", $"Will download {files.Count} files");

            var downloadedFiles = 0;
            var fileTasks = new Task[files.Count];

            for (var i = 0; i < fileTasks.Length; i++)
            {
                var file = files[i];
                fileTasks[i] = TaskManager.Run(async () =>
                {
                    hashes.TryGetValue(file.FileName, out var hash);

                    var fileState = await DownloadFile(job, file, hash);

                    if (fileState == EResult.OK || fileState == EResult.SameAsPreviousValue)
                    {
                        hashes[file.FileName] = file.FileHash;

                        downloadedFiles++;
                    }

                    if (fileState != EResult.SameAsPreviousValue)
                    {
                        // Do not write progress info to log file
                        Console.WriteLine($"{job.DepotName} [{downloadedFiles / (float) files.Count * 100.0f,6:#00.00}%] {files.Count - downloadedFiles} files left to download");
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
            }

            await Task.WhenAll(fileTasks).ConfigureAwait(false);

            if (downloadState == EResult.OK)
            {
                await File.WriteAllTextAsync(hashesFile, JsonConvert.SerializeObject(hashes));

                job.Result = EResult.OK;
            }
            else if (downloadState == EResult.DataCorruption)
            {
                job.Result = EResult.DataCorruption;
            }
            else
            {
                job.Result = EResult.Ignored;
            }

            return job.Result;
        }

        private static async Task<EResult> DownloadFile(DepotProcessor.ManifestJob job, DepotManifest.FileData file, byte[] hash)
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
                    await using (var _ = finalPath.Create())
                    {
                        // FileInfo.Create returns a stream but we don't need it
                    }

                    Log.WriteInfo($"FileDownloader {job.DepotID}", $"{file.FileName} created an empty file");

                    return EResult.SameAsPreviousValue;
                }
                else if (finalPath.Length == 0)
                {
#if DEBUG
                    Log.WriteDebug($"FileDownloader {job.DepotID}", $"{file.FileName} is already empty");
#endif

                    return EResult.SameAsPreviousValue;
                }
            }
            else if (hash != null && file.FileHash.SequenceEqual(hash))
            {
#if DEBUG
                Log.WriteDebug($"FileDownloader {job.DepotID}", $"{file.FileName} already matches the file we have");
#endif

                return EResult.SameAsPreviousValue;
            }

            var checksum = Utils.Sha1Instance.ComputeHash(Encoding.UTF8.GetBytes(file.FileName));

            var neededChunks = new List<DepotManifest.ChunkData>();
            var chunks = file.Chunks.OrderBy(x => x.Offset).ToList();
            var oldChunksFile = Path.Combine(Application.Path, "files", ".support", "chunks", $"{job.DepotID}-{BitConverter.ToString(checksum)}.json");

            await using (var fs = downloadPath.Open(FileMode.OpenOrCreate, FileAccess.ReadWrite))
            {
                fs.SetLength((long)file.TotalSize);

                if (finalPath.Exists && File.Exists(oldChunksFile))
                {
                    var oldChunks = JsonConvert.DeserializeObject<List<DepotManifest.ChunkData>>(await File.ReadAllTextAsync(oldChunksFile), JsonHandleAllReferences);

                    await using var fsOld = finalPath.Open(FileMode.Open, FileAccess.Read);

                    foreach (var chunk in chunks)
                    {
                        var oldChunk = oldChunks.Find(c => c.ChunkID.SequenceEqual(chunk.ChunkID));

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
                                Log.WriteDebug($"FileDownloader {job.DepotID}", $"{file.FileName} Found chunk ({chunk.Offset}), not downloading");
#endif
                            }
                            else
                            {
                                neededChunks.Add(chunk);

#if DEBUG
                                Log.WriteDebug($"FileDownloader {job.DepotID}", $"{file.FileName} Found chunk ({chunk.Offset}), but checksum differs");
#endif
                            }
                        }
                        else
                        {
                            neededChunks.Add(chunk);
                        }
                    }
                }
                else
                {
                    neededChunks = chunks;
                }
            }

            using var chunkCancellation = new CancellationTokenSource();
            var downloadedSize = file.TotalSize - (ulong)neededChunks.Sum(x => x.UncompressedLength);
            var chunkTasks = new Task[neededChunks.Count];

            Log.WriteInfo($"FileDownloader {job.DepotID}", $"Downloading {file.FileName} ({downloadedSize} bytes, {neededChunks.Count} out of {chunks.Count} chunks)");

            for (var i = 0; i < chunkTasks.Length; i++)
            {
                var chunk = neededChunks[i];
                chunkTasks[i] = TaskManager.Run(async () =>
                {
                    try
                    {
                        chunkCancellation.Token.ThrowIfCancellationRequested();

                        await ChunkDownloadingSemaphore.WaitAsync(chunkCancellation.Token).ConfigureAwait(false);

                        var result = await DownloadChunk(job, chunk, downloadPath);

                        if (!result)
                        {
                            Log.WriteWarn($"FileDownloader {job.DepotID}", $"Failed to download chunk for {file.FileName} ({chunk.Offset})");

                            chunkCancellation.Cancel();
                        }
                        else
                        {
                            downloadedSize += chunk.UncompressedLength;

                            // Do not write progress info to log file
                            Console.WriteLine($"{job.DepotName} [{downloadedSize / (float) file.TotalSize * 100.0f,6:#00.00}%] {file.FileName}");
                        }
                    }
                    finally
                    {
                        ChunkDownloadingSemaphore.Release();
                    }
                });
            }

            await Task.WhenAll(chunkTasks).ConfigureAwait(false);

            await using (var fs = downloadPath.Open(FileMode.Open, FileAccess.ReadWrite))
            {
                fs.Seek(0, SeekOrigin.Begin);

                checksum = Utils.Sha1Instance.ComputeHash(fs);
            }

            if (!file.FileHash.SequenceEqual(checksum))
            {
                IRC.Instance.SendOps($"{Colors.OLIVE}[{job.DepotName}]{Colors.NORMAL} Failed to correctly download {Colors.BLUE}{file.FileName}");

                Log.WriteWarn($"FileDownloader {job.DepotID}", $"Hash check failed for {file.FileName} ({job.Server})");

                downloadPath.Delete();

                return EResult.DataCorruption;
            }

            Log.WriteInfo($"FileDownloader {job.DepotID}", $"Downloaded {file.FileName}");

            finalPath.Delete();

            downloadPath.MoveTo(finalPath.FullName);

            if (chunks.Count > 1)
            {
                await File.WriteAllTextAsync(oldChunksFile, JsonConvert.SerializeObject(chunks, Formatting.None, JsonHandleAllReferences), chunkCancellation.Token);
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
                    var chunkData = await CDNClient.DownloadDepotChunkAsync(job.DepotID, chunk, job.Server, string.Empty, job.DepotKey);

                    await using var fs = downloadPath.Open(FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                    fs.Seek((long)chunk.Offset, SeekOrigin.Begin);
                    await fs.WriteAsync(chunkData.Data, 0, chunkData.Data.Length);

                    return true;
                }
                catch (Exception e)
                {
                    Log.WriteWarn($"FileDownloader {job.DepotID}", $"Exception: {e}");
                }

                if (i < 5)
                {
                    await Task.Delay(Utils.ExponentionalBackoff(i + 1));
                }
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
