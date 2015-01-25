/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SteamKit2;

namespace SteamDatabaseBackend
{
    class PICSProductInfo : SteamHandler
    {
        public PICSProductInfo(CallbackManager manager)
            : base(manager)
        {
            manager.Register(new Callback<SteamApps.PICSProductInfoCallback>(OnPICSProductInfo));
        }

        private static void OnPICSProductInfo(SteamApps.PICSProductInfoCallback callback)
        {
            JobAction job;

            if (JobManager.TryRemoveJob(callback.JobID, out job) && job.IsCommand)
            {
                OnProductInfoForIRC(job.CommandRequest, callback);
            }

            var apps = callback.Apps.Concat(callback.UnknownApps.ToDictionary(x => x, x => (SteamApps.PICSProductInfoCallback.PICSProductInfo)null));
            var packages = callback.Packages.Concat(callback.UnknownPackages.ToDictionary(x => x, x => (SteamApps.PICSProductInfoCallback.PICSProductInfo)null));

            foreach (var app in apps)
            {
                var workaround = app;

                Log.WriteInfo("PICSProductInfo", "{0}AppID: {1}", app.Value == null ? "Unknown " : "", app.Key);

                Task mostRecentItem;
                Application.ProcessedApps.TryGetValue(workaround.Key, out mostRecentItem);

                var workerItem = TaskManager.Run(async delegate
                {
                    if (mostRecentItem != null && !mostRecentItem.IsCompleted)
                    {
                        Log.WriteDebug("PICSProductInfo", "Waiting for app {0} to finish processing", workaround.Key);

                        await mostRecentItem;
                    }

                    var processor = new AppProcessor(workaround.Key);

                    if (workaround.Value == null)
                    {
                        processor.ProcessUnknown();
                    }
                    else
                    {
                        processor.Process(workaround.Value);
                    }
                });

                if (Settings.IsFullRun)
                {
                    continue;
                }

                Application.ProcessedApps.AddOrUpdate(app.Key, workerItem, (key, oldValue) => workerItem);

                workerItem.ContinueWith(task =>
                {
                    lock (Application.ProcessedApps)
                    {
                        if (Application.ProcessedApps.TryGetValue(workaround.Key, out mostRecentItem) && mostRecentItem.IsCompleted)
                        {
                            Application.ProcessedApps.TryRemove(workaround.Key, out mostRecentItem);
                        }
                    }
                });
            }

            foreach (var package in packages)
            {
                var workaround = package;

                Log.WriteInfo("PICSProductInfo", "{0}AppID: {1}", package.Value == null ? "Unknown " : "", package.Key);

                Task mostRecentItem;
                Application.ProcessedSubs.TryGetValue(workaround.Key, out mostRecentItem);

                var workerItem = TaskManager.Run(async delegate
                {
                    if (mostRecentItem != null && !mostRecentItem.IsCompleted)
                    {
                        Log.WriteDebug("PICSProductInfo", "Waiting for package {0} to finish processing", workaround.Key);

                        await mostRecentItem;
                    }

                    var processor = new SubProcessor(workaround.Key);

                    if (workaround.Value == null)
                    {
                        processor.ProcessUnknown();
                    }
                    else
                    {
                        processor.Process(workaround.Value);
                    }
                });

                if (Settings.IsFullRun)
                {
                    continue;
                }

                Application.ProcessedSubs.AddOrUpdate(package.Key, workerItem, (key, oldValue) => workerItem);

                workerItem.ContinueWith(task =>
                {
                    lock (Application.ProcessedSubs)
                    {
                        if (Application.ProcessedSubs.TryGetValue(workaround.Key, out mostRecentItem) && mostRecentItem.IsCompleted)
                        {
                            Application.ProcessedSubs.TryRemove(workaround.Key, out mostRecentItem);
                        }
                    }
                });
            }
        }

        private static void OnProductInfoForIRC(JobManager.IRCRequest request, SteamApps.PICSProductInfoCallback callback)
        {
            if (request.Type == JobManager.IRCRequestType.TYPE_SUB)
            {
                if (!callback.Packages.ContainsKey(request.Target))
                {
                    CommandHandler.ReplyToCommand(request.Command, "Unknown SubID: {0}{1}", Colors.BLUE, request.Target);

                    return;
                }

                var info = callback.Packages[request.Target];
                var kv = info.KeyValues.Children.FirstOrDefault();
                string name;

                if (kv["name"].Value != null)
                {
                    name = Utils.RemoveControlCharacters(kv["name"].AsString());
                }
                else
                {
                    name = Steam.GetPackageName(info.ID);
                }

                try
                {
                    kv.SaveToFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sub", string.Format("{0}.vdf", info.ID)), false);
                }
                catch (Exception e)
                {
                    CommandHandler.ReplyToCommand(request.Command, "Unable to save file for {0}: {1}", name, e.Message);

                    return;
                }

                CommandHandler.ReplyToCommand(request.Command, "{0}{1}{2} -{3} {4}{5} - Dump:{6} {7}{8}{9}{10}",
                    Colors.BLUE, name, Colors.NORMAL,
                    Colors.DARKBLUE, SteamDB.GetPackageURL(info.ID), Colors.NORMAL,
                    Colors.DARKBLUE, SteamDB.GetRawPackageURL(info.ID), Colors.NORMAL,
                    info.MissingToken ? SteamDB.StringNeedToken : string.Empty,
                    Application.OwnedSubs.ContainsKey(info.ID) ? SteamDB.StringCheckmark : string.Empty
                );
            }
            else if (request.Type == JobManager.IRCRequestType.TYPE_APP)
            {
                if (!callback.Apps.ContainsKey(request.Target))
                {
                    CommandHandler.ReplyToCommand(request.Command, "Unknown AppID: {0}{1}", Colors.BLUE, request.Target);

                    return;
                }

                var info = callback.Apps[request.Target];
                string name;

                if (info.KeyValues["common"]["name"].Value != null)
                {
                    name = Utils.RemoveControlCharacters(info.KeyValues["common"]["name"].AsString());
                }
                else
                {
                    name = Steam.GetAppName(info.ID);
                }

                try
                {
                    info.KeyValues.SaveToFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app", string.Format("{0}.vdf", info.ID)), false);
                }
                catch (Exception e)
                {
                    CommandHandler.ReplyToCommand(request.Command, "Unable to save file for {0}: {1}", name, e.Message);

                    return;
                }

                CommandHandler.ReplyToCommand(request.Command, "{0}{1}{2} -{3} {4}{5} - Dump:{6} {7}{8}{9}{10}",
                    Colors.BLUE, name, Colors.NORMAL,
                    Colors.DARKBLUE, SteamDB.GetAppURL(info.ID), Colors.NORMAL,
                    Colors.DARKBLUE, SteamDB.GetRawAppURL(info.ID), Colors.NORMAL,
                    info.MissingToken ? SteamDB.StringNeedToken : string.Empty,
                    Application.OwnedApps.ContainsKey(info.ID) ? SteamDB.StringCheckmark : string.Empty
                );
            }
            else
            {
                CommandHandler.ReplyToCommand(request.Command, "I have no idea what happened here!");
            }
        }
    }
}
