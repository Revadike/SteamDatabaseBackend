/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Amib.Threading;
using MySql.Data.MySqlClient;
using SteamKit2;

namespace SteamDatabaseBackend
{
    class PICSProductInfo : SteamHandler
    {
        private static readonly Dictionary<uint, IWorkItemResult> ProcessedApps;
        private static readonly Dictionary<uint, IWorkItemResult> ProcessedSubs;
        public static SmartThreadPool ProcessorThreadPool { get; private set; }

        static PICSProductInfo()
        {
            ProcessedApps = new Dictionary<uint, IWorkItemResult>();
            ProcessedSubs = new Dictionary<uint, IWorkItemResult>();

            ProcessorThreadPool = new SmartThreadPool();
            ProcessorThreadPool.Concurrency = 50;
            ProcessorThreadPool.Name = "App/Sub Thread Pool";
        }

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

                IWorkItemResult mostRecentItem;

                lock (ProcessedApps)
                {
                    ProcessedApps.TryGetValue(app.Key, out mostRecentItem);
                }

                var workerItem = ProcessorThreadPool.QueueWorkItem(delegate
                {
                    try
                    {
                        if (mostRecentItem != null && !mostRecentItem.IsCompleted)
                        {
                            Log.WriteDebug("PICSProductInfo", "Waiting for app {0} to finish processing", app.Key);

                            SmartThreadPool.WaitAll(new IWaitableResult[] { mostRecentItem });
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
                        Log.WriteError("PICSProductInfo", "App {0} faulted: {1}", app.Key, e);

                        JobManager.AddJob(() => Steam.Instance.Apps.PICSGetAccessTokens(app.Key, null));
                    }
                    catch (Exception e)
                    {
                        Log.WriteError("PICSProductInfo", "App {0} faulted: {1}", app.Key, e);
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

                IWorkItemResult mostRecentItem;

                lock (ProcessedSubs)
                {
                    ProcessedSubs.TryGetValue(package.Key, out mostRecentItem);
                }

                var workerItem = ProcessorThreadPool.QueueWorkItem(delegate
                {
                    try
                    {
                        if (mostRecentItem != null && !mostRecentItem.IsCompleted)
                        {
                            Log.WriteDebug("PICSProductInfo", "Waiting for package {0} to finish processing", package.Key);

                            SmartThreadPool.WaitAll(new IWaitableResult[] { mostRecentItem });
                        }

                        using (var processor = new SubProcessor(package.Key))
                        {
                            if (package.Value == null)
                            {
                                processor.ProcessUnknown();
                            }
                            else
                            {
                                processor.Process(package.Value);
                            }
                        }
                    }
                    catch (MySqlException e)
                    {
                        Log.WriteError("PICSProductInfo", "Package {0} faulted: {1}", package.Key, e);

                        JobManager.AddJob(() => Steam.Instance.Apps.PICSGetProductInfo(null, package.Key, false, false));
                    }
                    catch (Exception e)
                    {
                        Log.WriteError("PICSProductInfo", "Package {0} faulted: {1}", package.Key, e);
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
