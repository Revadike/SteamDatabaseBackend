/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using SteamKit2;

namespace SteamDatabaseBackend
{
    class PICSProductInfo : SteamHandler
    {
        public static ConcurrentDictionary<uint, Task> ProcessedApps { get; private set; }
        public static ConcurrentDictionary<uint, Task> ProcessedSubs { get; private set; }

        static PICSProductInfo()
        {
            ProcessedApps = new ConcurrentDictionary<uint, Task>();
            ProcessedSubs = new ConcurrentDictionary<uint, Task>();
        }

        public PICSProductInfo(CallbackManager manager)
            : base(manager)
        {
            manager.Register(new Callback<SteamApps.PICSProductInfoCallback>(OnPICSProductInfo));
        }

        private static void OnPICSProductInfo(SteamApps.PICSProductInfoCallback callback)
        {
            var apps = callback.Apps.Concat(callback.UnknownApps.ToDictionary(x => x, x => (SteamApps.PICSProductInfoCallback.PICSProductInfo)null));
            var packages = callback.Packages.Concat(callback.UnknownPackages.ToDictionary(x => x, x => (SteamApps.PICSProductInfoCallback.PICSProductInfo)null));

            foreach (var app in apps)
            {
                var workaround = app;

                Log.WriteInfo("PICSProductInfo", "{0}AppID: {1}", app.Value == null ? "Unknown " : "", app.Key);

                Task mostRecentItem;
                ProcessedApps.TryGetValue(workaround.Key, out mostRecentItem);

                var workerItem = TaskManager.Run(async delegate
                {
                    if (mostRecentItem != null && !mostRecentItem.IsCompleted)
                    {
                        Log.WriteDebug("PICSProductInfo", "Waiting for app {0} to finish processing", workaround.Key);

                        await mostRecentItem;
                    }

                    using (var processor = new AppProcessor(workaround.Key))
                    {
                        if (workaround.Value == null)
                        {
                            processor.ProcessUnknown();
                        }
                        else
                        {
                            processor.Process(workaround.Value);
                        }
                    }
                });

                if (Settings.IsFullRun)
                {
                    continue;
                }

                ProcessedApps.AddOrUpdate(app.Key, workerItem, (key, oldValue) => workerItem);

                workerItem.ContinueWith(task =>
                {
                    lock (ProcessedApps)
                    {
                        if (ProcessedApps.TryGetValue(workaround.Key, out mostRecentItem) && mostRecentItem.IsCompleted)
                        {
                            ProcessedApps.TryRemove(workaround.Key, out mostRecentItem);
                        }
                    }
                });
            }

            foreach (var package in packages)
            {
                var workaround = package;

                Log.WriteInfo("PICSProductInfo", "{0}AppID: {1}", package.Value == null ? "Unknown " : "", package.Key);

                Task mostRecentItem;
                ProcessedSubs.TryGetValue(workaround.Key, out mostRecentItem);

                var workerItem = TaskManager.Run(async delegate
                {
                    if (mostRecentItem != null && !mostRecentItem.IsCompleted)
                    {
                        Log.WriteDebug("PICSProductInfo", "Waiting for package {0} to finish processing", workaround.Key);

                        await mostRecentItem;
                    }

                    using (var processor = new SubProcessor(workaround.Key))
                    {
                        if (workaround.Value == null)
                        {
                            processor.ProcessUnknown();
                        }
                        else
                        {
                            processor.Process(workaround.Value);
                        }
                    }
                });

                if (Settings.IsFullRun)
                {
                    continue;
                }

                ProcessedSubs.AddOrUpdate(package.Key, workerItem, (key, oldValue) => workerItem);

                workerItem.ContinueWith(task =>
                {
                    lock (ProcessedSubs)
                    {
                        if (ProcessedSubs.TryGetValue(workaround.Key, out mostRecentItem) && mostRecentItem.IsCompleted)
                        {
                            ProcessedSubs.TryRemove(workaround.Key, out mostRecentItem);
                        }
                    }
                });
            }
        }
    }
}
