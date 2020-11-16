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
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Newtonsoft.Json;
using SteamKit2;

namespace SteamDatabaseBackend
{
    internal static class FileDownloader
    {
        private class ExistingFileData
        {
            public byte[] FileHash { get; set; }
            public Dictionary<ulong, byte[]> Chunks { get; set; } = new Dictionary<ulong, byte[]>();
        }

        private static readonly SemaphoreSlim ChunkDownloadingSemaphore = new SemaphoreSlim(12);
        private static readonly Dictionary<uint, Regex> Files = new Dictionary<uint, Regex>();
        private static Dictionary<uint, string> DownloadFolders = new Dictionary<uint, string>();
        private static CDNClient CDNClient;

        public static void SetCDNClient(CDNClient cdnClient)
        {
            CDNClient = cdnClient;

            var file = Path.Combine(Application.Path, "files", "depots_mapping.json");

            if (!File.Exists(file))
            {
                Log.WriteWarn(nameof(FileDownloader), "files/depots_mapping.json not found.");

                return;
            }

            DownloadFolders = JsonConvert.DeserializeObject<Dictionary<uint, string>>(File.ReadAllText(file));

            file = Path.Combine(Application.Path, "files", "files.json");

            if (!File.Exists(file))
            {
                Log.WriteWarn(nameof(FileDownloader), "files/files.json not found. No files will be downloaded.");

                return;
            }

            var files = JsonConvert.DeserializeObject<Dictionary<uint, List<string>>>(File.ReadAllText(file));

            foreach (var (depotid, fileMatches) in files)
            {
                if (!DownloadFolders.ContainsKey(depotid))
                {
                    throw new InvalidDataException($"Missing depot mapping for depotid {depotid}.");
                }

                var pattern = $"^({string.Join("|", fileMatches.Select(ConvertFileMatch))})$";

                Files[depotid] = new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);
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
            var filesRegex = Files[job.DepotID];
            var files = depotManifest.Files.Where(x => filesRegex.IsMatch(x.FileName.Replace('\\', '/'))).ToList();
            var downloadState = EResult.Fail;

            ConcurrentDictionary<string, ExistingFileData> existingFileData;

            await using (var db = await Database.GetConnectionAsync())
            {
                var data = db.ExecuteScalar<string>("SELECT `Value` FROM `LocalConfig` WHERE `ConfigKey` = @Key", new { Key = $"depot.{job.DepotID}" });

                if (data != null)
                {
                    existingFileData = JsonConvert.DeserializeObject<ConcurrentDictionary<string, ExistingFileData>>(data);
                }
                else
                {
                    existingFileData = new ConcurrentDictionary<string, ExistingFileData>();
                }
            }

            foreach (var file in existingFileData.Keys.Except(files.Select(x => x.FileName)))
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
                    var existingFile = existingFileData.GetOrAdd(file.FileName, _ => new ExistingFileData());
                    EResult fileState;

                    try
                    {
                        await ChunkDownloadingSemaphore.WaitAsync().ConfigureAwait(false);

                        fileState = await DownloadFile(job, file, existingFile);
                    }
                    finally
                    {
                        ChunkDownloadingSemaphore.Release();
                    }

                    if (fileState == EResult.OK || fileState == EResult.SameAsPreviousValue)
                    {
                        existingFile.FileHash = file.FileHash;

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

            await LocalConfig.Update($"depot.{job.DepotID}", JsonConvert.SerializeObject(existingFileData));

            job.Result = downloadState switch
            {
                EResult.OK => EResult.OK,
                EResult.DataCorruption => EResult.DataCorruption,
                _ => EResult.Ignored
            };

            return job.Result;
        }

        private static async Task<EResult> DownloadFile(DepotProcessor.ManifestJob job, DepotManifest.FileData file, ExistingFileData existingFile)
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
            else if (existingFile.FileHash != null && file.FileHash.SequenceEqual(existingFile.FileHash))
            {
#if DEBUG
                Log.WriteDebug($"FileDownloader {job.DepotID}", $"{file.FileName} already matches the file we have");
#endif

                return EResult.SameAsPreviousValue;
            }
            
            using var sha = SHA1.Create();

            var neededChunks = new List<DepotManifest.ChunkData>();
            var chunks = file.Chunks.OrderBy(x => x.Offset).ToList();

            await using (var fs = downloadPath.Open(FileMode.OpenOrCreate, FileAccess.ReadWrite))
            {
                fs.SetLength((long)file.TotalSize);

                if (finalPath.Exists)
                {
                    await using var fsOld = finalPath.Open(FileMode.Open, FileAccess.Read);

                    foreach (var chunk in chunks)
                    {
                        var oldChunk = existingFile.Chunks.FirstOrDefault(c => c.Value.SequenceEqual(chunk.ChunkID));

                        if (oldChunk.Value != null)
                        {
                            var oldData = new byte[chunk.UncompressedLength];
                            fsOld.Seek((long)oldChunk.Key, SeekOrigin.Begin);
                            fsOld.Read(oldData, 0, oldData.Length);

                            var existingChecksum = sha.ComputeHash(oldData);

                            if (existingChecksum.SequenceEqual(chunk.ChunkID))
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

            Log.WriteInfo($"FileDownloader {job.DepotID}", $"Downloading {file.FileName} ({neededChunks.Count} out of {chunks.Count} chunks to download)");

            for (var i = 0; i < chunkTasks.Length; i++)
            {
                var chunk = neededChunks[i];
                chunkTasks[i] = TaskManager.Run(async () =>
                {
                    var result = await DownloadChunk(job, chunk, downloadPath, chunkCancellation);

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
                });
            }

            await Task.WhenAll(chunkTasks).ConfigureAwait(false);

            byte[] checksum;

            await using (var fs = downloadPath.Open(FileMode.Open, FileAccess.ReadWrite))
            {
                checksum = await sha.ComputeHashAsync(fs, chunkCancellation.Token);
            }

            if (!file.FileHash.SequenceEqual(checksum))
            {
                if (!job.DownloadCorrupted)
                {
                    job.DownloadCorrupted = true;

                    IRC.Instance.SendOps($"{Colors.OLIVE}[{job.DepotName}]{Colors.NORMAL} Failed to correctly download {Colors.BLUE}{file.FileName}");
                }

                Log.WriteWarn($"FileDownloader {job.DepotID}", $"Hash check failed for {file.FileName} ({job.Server})");

                downloadPath.Delete();
                existingFile.FileHash = null;
                existingFile.Chunks.Clear();

                return EResult.DataCorruption;
            }

            Log.WriteInfo($"FileDownloader {job.DepotID}", $"Downloaded {file.FileName}");

            finalPath.Delete();

            downloadPath.MoveTo(finalPath.FullName);

            if (chunks.Count > 0)
            {
                existingFile.Chunks = chunks.ToDictionary(chunk => chunk.Offset, chunk => chunk.ChunkID);
            }
            else
            {
                existingFile.Chunks.Clear();
            }

            return EResult.OK;
        }

        private static async Task<bool> DownloadChunk(DepotProcessor.ManifestJob job, DepotManifest.ChunkData chunk, FileInfo downloadPath, CancellationTokenSource chunkCancellation)
        {
            const int TRIES = 3;

            for (var i = 0; i <= TRIES; i++)
            {
                chunkCancellation.Token.ThrowIfCancellationRequested();

                try
                {
                    var chunkData = await CDNClient.DownloadDepotChunkAsync(job.DepotID, chunk, job.Server, string.Empty, job.DepotKey);

                    await using var fs = downloadPath.Open(FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                    fs.Seek((long)chunk.Offset, SeekOrigin.Begin);
                    await fs.WriteAsync(chunkData.Data);

                    return true;
                }
                catch (Exception e)
                {
                    Log.WriteWarn($"FileDownloader {job.DepotID}", $"Exception: {e}");
                }

                if (i < TRIES)
                {
                    await Task.Delay(Utils.ExponentionalBackoff(i + 1));
                }
            }

            return false;
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
