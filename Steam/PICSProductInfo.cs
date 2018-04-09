/*
 * Copyright (c) 2013-2018, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
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
                    try
                    {
                        await Semaphore.WaitAsync(TaskManager.TaskCancellationToken.Token).ConfigureAwait(false);

                        if (mostRecentItem != null && !mostRecentItem.IsCompleted)
                        {
                            Log.WriteDebug(processor.ToString(), $"Waiting for previous task to finish processing ({CurrentlyProcessing.Count})");

                            await mostRecentItem.ConfigureAwait(false);

#if DEBUG
                            Log.WriteDebug(processor.ToString(), "Previous task lock ended");
#endif
                        }

                        await processor.Process().ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        ErrorReporter.Notify(processor.ToString(), e);
                    }
                    finally
                    {
                        Semaphore.Release();

                        processor.Dispose();
                    }

                    return processor;
                }).Unwrap();

                lock (CurrentlyProcessing)
                {
                    CurrentlyProcessing[processor.Id] = workerItem;
                }

                // Register error handler on inner task and the continuation
                TaskManager.RegisterErrorHandler(workerItem);
                TaskManager.RegisterErrorHandler(workerItem.ContinueWith(RemoveProcessorLock, TaskManager.TaskCancellationToken.Token));
            }
        }

        private static void RemoveProcessorLock(Task<BaseProcessor> task)
        {
            var processor = task.Result;

            lock (CurrentlyProcessing)
            {
                if (CurrentlyProcessing.TryGetValue(processor.Id, out var mostRecentItem) && mostRecentItem.IsCompleted)
                {
                    CurrentlyProcessing.Remove(processor.Id);

#if DEBUG
                    Log.WriteDebug(processor.ToString(), $"Removed completed lock ({CurrentlyProcessing.Count})");
#endif
                }
            }
        }
    }
}
