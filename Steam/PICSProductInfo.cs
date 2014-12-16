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

            foreach (var app in callback.Apps)
            {
                Log.WriteInfo("PICSProductInfo", "AppID: {0}", app.Key);

                var workaround = app;

                Task mostRecentItem;
                Application.ProcessedApps.TryGetValue(workaround.Key, out mostRecentItem);

                var workerItem = TaskManager.Run(async delegate
                {
                    if (mostRecentItem != null && !mostRecentItem.IsCompleted)
                    {
                        Log.WriteDebug("PICSProductInfo", "Waiting for app {0} to finish processing", workaround.Key);

                        await mostRecentItem;
                    }

                    new AppProcessor(workaround.Key).Process(workaround.Value);
                });

                if (!Settings.IsFullRun)
                {
                    Application.ProcessedApps.AddOrUpdate(app.Key, workerItem, (key, oldValue) => workerItem);
                }

                workerItem.ContinueWith(task =>
                {
                    if (Application.ProcessedApps.TryGetValue(workaround.Key, out mostRecentItem) && mostRecentItem.IsCompleted)
                    {
                        Application.ProcessedApps.TryRemove(workaround.Key, out mostRecentItem);
                    }
                });
            }

            foreach (var package in callback.Packages)
            {
                Log.WriteInfo("PICSProductInfo", "SubID: {0}", package.Key);

                var workaround = package;

                Task mostRecentItem;
                Application.ProcessedSubs.TryGetValue(workaround.Key, out mostRecentItem);

                var workerItem = TaskManager.Run(async delegate
                {
                    if (mostRecentItem != null && !mostRecentItem.IsCompleted)
                    {
                        Log.WriteDebug("PICSProductInfo", "Waiting for package {0} to finish processing", workaround.Key);

                        await mostRecentItem;
                    }

                    new SubProcessor(workaround.Key).Process(workaround.Value);
                });

                if (!Settings.IsFullRun)
                {
                    Application.ProcessedSubs.AddOrUpdate(package.Key, workerItem, (key, oldValue) => workerItem);
                }

                workerItem.ContinueWith(task =>
                {
                    if (Application.ProcessedSubs.TryGetValue(workaround.Key, out mostRecentItem) && mostRecentItem.IsCompleted)
                    {
                        Application.ProcessedSubs.TryRemove(workaround.Key, out mostRecentItem);
                    }
                });
            }

            foreach (uint app in callback.UnknownApps)
            {
                Log.WriteInfo("PICSProductInfo", "Unknown AppID: {0}", app);

                uint workaround = app;

                Task mostRecentItem;
                Application.ProcessedApps.TryGetValue(workaround, out mostRecentItem);

                var workerItem = TaskManager.Run(async delegate
                {
                    if (mostRecentItem != null && !mostRecentItem.IsCompleted)
                    {
                        Log.WriteDebug("PICSProductInfo", "Waiting for app {0} to finish processing (unknown)", workaround);

                        await mostRecentItem;
                    }

                    new AppProcessor(workaround).ProcessUnknown();
                });

                if (!Settings.IsFullRun)
                {
                    Application.ProcessedApps.AddOrUpdate(app, workerItem, (key, oldValue) => workerItem);
                }

                workerItem.ContinueWith(task =>
                {
                    if (Application.ProcessedApps.TryGetValue(workaround, out mostRecentItem) && mostRecentItem.IsCompleted)
                    {
                        Application.ProcessedApps.TryRemove(workaround, out mostRecentItem);
                    }
                });
            }

            foreach (uint package in callback.UnknownPackages)
            {
                Log.WriteInfo("PICSProductInfo", "Unknown SubID: {0}", package);

                uint workaround = package;

                Task mostRecentItem;
                Application.ProcessedSubs.TryGetValue(workaround, out mostRecentItem);

                var workerItem = TaskManager.Run(async delegate
                {
                    if (mostRecentItem != null && !mostRecentItem.IsCompleted)
                    {
                        Log.WriteDebug("PICSProductInfo", "Waiting for package {0} to finish processing (unknown)", workaround);

                        await mostRecentItem;
                    }

                    new SubProcessor(workaround).ProcessUnknown();
                });

                if (!Settings.IsFullRun)
                {
                    Application.ProcessedSubs.AddOrUpdate(package, workerItem, (key, oldValue) => workerItem);
                }

                workerItem.ContinueWith(task =>
                {
                    if (Application.ProcessedSubs.TryGetValue(workaround, out mostRecentItem) && mostRecentItem.IsCompleted)
                    {
                        Application.ProcessedSubs.TryRemove(workaround, out mostRecentItem);
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
                string name = string.Format("SubID {0}", info.ID);

                if (kv["name"].Value != null)
                {
                    name = Utils.RemoveControlCharacters(kv["name"].AsString());
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
                string name = string.Format("AppID {0}", info.ID);

                if (info.KeyValues["common"]["name"].Value != null)
                {
                    name = Utils.RemoveControlCharacters(info.KeyValues["common"]["name"].AsString());
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
