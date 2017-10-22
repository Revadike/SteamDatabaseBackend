/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MySql.Data.MySqlClient;
using SteamKit2;

namespace SteamDatabaseBackend
{
    class PICSProductInfo : SteamHandler
    {
        private static readonly Dictionary<uint, Task> ProcessedApps = new Dictionary<uint, Task>();
        private static readonly Dictionary<uint, Task> ProcessedSubs = new Dictionary<uint, Task>();
        private static readonly SemaphoreSlim ProcessorSemaphore = new SemaphoreSlim(15);

        public PICSProductInfo(CallbackManager manager)
            : base(manager)
        {
            manager.Subscribe<SteamApps.PICSProductInfoCallback>(OnPICSProductInfo);
        }

        private static void OnPICSProductInfo(SteamApps.PICSProductInfoCallback callback)
        {
            JobManager.TryRemoveJob(callback.JobID);

            var apps = callback.Apps.Concat(callback.UnknownApps.ToDictionary(x => x, x => (SteamApps.PICSProductInfoCallback.PICSProductInfo)null));
            var packages = callback.Packages.Concat(callback.UnknownPackages.ToDictionary(x => x, x => (SteamApps.PICSProductInfoCallback.PICSProductInfo)null));

            foreach (var workaround in apps)
            {
                var app = workaround;

                Log.WriteInfo("PICSProductInfo", "{0}AppID: {1}", app.Value == null ? "Unknown " : "", app.Key);

                Task mostRecentItem;

                lock (ProcessedApps)
                {
                    ProcessedApps.TryGetValue(app.Key, out mostRecentItem);
                }

                var workerItem = TaskManager.Run(async () =>
                {
                    try
                    {
                        await ProcessorSemaphore.WaitAsync().ConfigureAwait(false);

                        if (mostRecentItem != null && !mostRecentItem.IsCompleted)
                        {
                            Log.WriteDebug("PICSProductInfo", "Waiting for app {0} to finish processing", app.Key);

                            await mostRecentItem.ConfigureAwait(false);
                        }

                        using (var processor = new AppProcessor(app.Key))
                        {
                            if (app.Value == null)
                            {
                                processor.ProcessUnknown();
                            }
                            else
                            {
                                processor.Process(app.Value);
                            }
                        }
                    }
                    catch (MySqlException e)
                    {
                        ErrorReporter.Notify($"App {app.Key}", e);

                        JobManager.AddJob(() => Steam.Instance.Apps.PICSGetAccessTokens(app.Key, null));
                    }
                    catch (Exception e)
                    {
                        ErrorReporter.Notify($"App {app.Key}", e);
                    }
                    finally
                    {
                        lock (ProcessedApps)
                        {
                            if (ProcessedApps.TryGetValue(app.Key, out mostRecentItem) && mostRecentItem.IsCompleted)
                            {
                                ProcessedApps.Remove(app.Key);
                            }
                        }

                        ProcessorSemaphore.Release();
                    }
                });

                if (Settings.IsFullRun)
                {
                    continue;
                }

                lock (ProcessedApps)
                {
                    ProcessedApps[app.Key] = workerItem;
                }
            }

            foreach (var workaround in packages)
            {
                var package = workaround;

                Log.WriteInfo("PICSProductInfo", "{0}SubID: {1}", package.Value == null ? "Unknown " : "", package.Key);

                Task mostRecentItem;

                lock (ProcessedSubs)
                {
                    ProcessedSubs.TryGetValue(package.Key, out mostRecentItem);
                }

                var workerItem = TaskManager.Run(async () =>
                {
                    try
                    {
                        await ProcessorSemaphore.WaitAsync().ConfigureAwait(false);

                        if (mostRecentItem != null && !mostRecentItem.IsCompleted)
                        {
                            Log.WriteDebug("PICSProductInfo", "Waiting for package {0} to finish processing", package.Key);

                            await mostRecentItem.ConfigureAwait(false);
                        }

                        using (var processor = new SubProcessor(package.Key))
                        {
                            if (package.Value == null)
                            {
                                await processor.ProcessUnknown();
                            }
                            else
                            {
                                await processor.Process(package.Value);
                            }
                        }
                    }
                    catch (MySqlException e)
                    {
                        ErrorReporter.Notify($"Package {package.Key}", e);

                        JobManager.AddJob(() => Steam.Instance.Apps.PICSGetProductInfo(null, package.Key, false, false));
                    }
                    catch (Exception e)
                    {
                        ErrorReporter.Notify($"Package {package.Key}", e);
                    }
                    finally
                    {
                        lock (ProcessedSubs)
                        {
                            if (ProcessedSubs.TryGetValue(package.Key, out mostRecentItem) && mostRecentItem.IsCompleted)
                            {
                                ProcessedSubs.Remove(package.Key);
                            }
                        }

                        ProcessorSemaphore.Release();
                    }
                });

                if (Settings.IsFullRun)
                {
                    continue;
                }

                lock (ProcessedSubs)
                {
                    ProcessedSubs[package.Key] = workerItem;
                }
            }
        }
    }
}
