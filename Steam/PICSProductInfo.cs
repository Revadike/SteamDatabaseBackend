/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SteamKit2;

namespace SteamDatabaseBackend
{
    class PICSProductInfo : SteamHandler
    {
        public static Dictionary<uint, Task> ProcessedApps { get; private set; }
        public static Dictionary<uint, Task> ProcessedSubs { get; private set; }

        static PICSProductInfo()
        {
            ProcessedApps = new Dictionary<uint, Task>();
            ProcessedSubs = new Dictionary<uint, Task>();
        }

        public PICSProductInfo(CallbackManager manager)
            : base(manager)
        {
            manager.Subscribe<SteamApps.PICSProductInfoCallback>(OnPICSProductInfo);
        }

        private static void OnPICSProductInfo(SteamApps.PICSProductInfoCallback callback)
        {
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

                var workerItem = TaskManager.Run(async delegate
                {
                    if (mostRecentItem != null && !mostRecentItem.IsCompleted)
                    {
                        Log.WriteDebug("PICSProductInfo", "Waiting for app {0} to finish processing", app.Key);

                        await mostRecentItem;
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
                });

                if (Settings.IsFullRun)
                {
                    continue;
                }


                lock (ProcessedApps)
                {
                    ProcessedApps[app.Key] = workerItem;
                }

                workerItem.ContinueWith(task =>
                {
                    lock (ProcessedApps)
                    {
                        if (ProcessedApps.TryGetValue(app.Key, out mostRecentItem) && mostRecentItem.IsCompleted)
                        {
                            ProcessedApps.Remove(app.Key);
                        }
                    }
                });
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

                var workerItem = TaskManager.Run(async delegate
                {
                    if (mostRecentItem != null && !mostRecentItem.IsCompleted)
                    {
                        Log.WriteDebug("PICSProductInfo", "Waiting for package {0} to finish processing", package.Key);

                        await mostRecentItem;
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
                });

                if (Settings.IsFullRun)
                {
                    continue;
                }

                lock (ProcessedSubs)
                {
                    ProcessedSubs[package.Key] = workerItem;
                }

                workerItem.ContinueWith(task =>
                {
                    lock (ProcessedSubs)
                    {
                        if (ProcessedSubs.TryGetValue(package.Key, out mostRecentItem) && mostRecentItem.IsCompleted)
                        {
                            ProcessedSubs.Remove(package.Key);
                        }
                    }
                });
            }
        }
    }
}
