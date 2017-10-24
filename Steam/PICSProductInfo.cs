/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;

namespace SteamDatabaseBackend
{
    class PICSProductInfo : SteamHandler
    {
        private static readonly Dictionary<uint, Task> CurrentlyProcessing = new Dictionary<uint, Task>();
        private static readonly SemaphoreSlim Semaphore = new SemaphoreSlim(15);

        public static int CurrentlyProcessingCount
        {
            get
            {
                lock (CurrentlyProcessing)
                {
                    return CurrentlyProcessing.Count;
                }
            }
        }

        public PICSProductInfo(CallbackManager manager)
            : base(manager)
        {
            manager.Subscribe<SteamApps.PICSProductInfoCallback>(OnPICSProductInfo);
        }

        private static void OnPICSProductInfo(SteamApps.PICSProductInfoCallback callback)
        {
            JobManager.TryRemoveJob(callback.JobID);
            
            var processors = new List<BaseProcessor>(
                callback.Apps.Count +
                callback.Packages.Count +
                callback.UnknownApps.Count +
                callback.UnknownPackages.Count
            );
            processors.AddRange(callback.Apps.Select(app => new AppProcessor(app.Key, app.Value)));
            processors.AddRange(callback.Packages.Select(package => new SubProcessor(package.Key, package.Value)));
            processors.AddRange(callback.UnknownApps.Select(app => new AppProcessor(app, null)));
            processors.AddRange(callback.UnknownPackages.Select(package => new SubProcessor(package, null)));
            
            foreach (var workaround in processors)
            {
                var processor = workaround;

                Task mostRecentItem;

                lock (CurrentlyProcessing)
                {
                    CurrentlyProcessing.TryGetValue(processor.Id, out mostRecentItem);
                }

                var workerItem = TaskManager.Run(async () =>
                {
                    await Semaphore.WaitAsync(TaskManager.TaskCancellationToken.Token).ConfigureAwait(false);

                    try
                    {
                        if (mostRecentItem != null && !mostRecentItem.IsCompleted)
                        {
                            Log.WriteDebug("PICSProductInfo", "Waiting for {0} to finish processing", processor.ToString());

                            await mostRecentItem.ConfigureAwait(false);
                        }

                        await processor.Process().ConfigureAwait(false);
                    }
                    finally
                    {
                        Semaphore.Release();
                    }
                }).Unwrap();

                lock (CurrentlyProcessing)
                {
                    CurrentlyProcessing[processor.Id] = workerItem;
                }

                workerItem.ContinueWith(task =>
                {
                    lock (CurrentlyProcessing)
                    {
                        if (CurrentlyProcessing.TryGetValue(processor.Id, out mostRecentItem) && mostRecentItem.IsCompleted)
                        {
                            CurrentlyProcessing.Remove(processor.Id);
                        }
                    }
                }, TaskManager.TaskCancellationToken.Token);
            }
        }
    }
}
