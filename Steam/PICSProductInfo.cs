/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.IO;
using System.Linq;
using Amib.Threading;
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
                Log.WriteInfo("Steam", "AppID: {0}", app.Key);

                var workaround = app;

                IWorkItemResult mostRecentItem;
                Application.Instance.ProcessedApps.TryGetValue(workaround.Key, out mostRecentItem);

                var workerItem = Application.Instance.ProcessorPool.QueueWorkItem(delegate
                {
                    if (mostRecentItem != null && !mostRecentItem.IsCompleted)
                    {
                        Log.WriteDebug("Steam", "Waiting for app {0} to finish processing", workaround.Key);

                        SmartThreadPool.WaitAll(new IWaitableResult[] { mostRecentItem });
                    }

                    new AppProcessor(workaround.Key).Process(workaround.Value);
                });

                Application.Instance.ProcessedApps.AddOrUpdate(app.Key, workerItem, (key, oldValue) => workerItem);
            }

            foreach (var package in callback.Packages)
            {
                Log.WriteInfo("Steam", "SubID: {0}", package.Key);

                var workaround = package;

                IWorkItemResult mostRecentItem;
                Application.Instance.ProcessedSubs.TryGetValue(workaround.Key, out mostRecentItem);

                var workerItem = Application.Instance.ProcessorPool.QueueWorkItem(delegate
                {
                    if (mostRecentItem != null && !mostRecentItem.IsCompleted)
                    {
                        Log.WriteDebug("Steam", "Waiting for package {0} to finish processing", workaround.Key);

                        SmartThreadPool.WaitAll(new IWaitableResult[] { mostRecentItem });
                    }

                    new SubProcessor(workaround.Key).Process(workaround.Value);
                });

                Application.Instance.ProcessedSubs.AddOrUpdate(package.Key, workerItem, (key, oldValue) => workerItem);
            }

            foreach (uint app in callback.UnknownApps)
            {
                Log.WriteInfo("Steam", "Unknown AppID: {0}", app);

                uint workaround = app;

                IWorkItemResult mostRecentItem;
                Application.Instance.ProcessedApps.TryGetValue(workaround, out mostRecentItem);

                var workerItem = Application.Instance.ProcessorPool.QueueWorkItem(delegate
                {
                    if (mostRecentItem != null && !mostRecentItem.IsCompleted)
                    {
                        Log.WriteDebug("Steam", "Waiting for app {0} to finish processing (unknown)", workaround);

                        SmartThreadPool.WaitAll(new IWaitableResult[] { mostRecentItem });
                    }

                    new AppProcessor(workaround).ProcessUnknown();
                });

                Application.Instance.ProcessedApps.AddOrUpdate(app, workerItem, (key, oldValue) => workerItem);
            }

            foreach (uint package in callback.UnknownPackages)
            {
                Log.WriteInfo("Steam", "Unknown SubID: {0}", package);

                uint workaround = package;

                IWorkItemResult mostRecentItem;
                Application.Instance.ProcessedSubs.TryGetValue(workaround, out mostRecentItem);

                var workerItem = Application.Instance.ProcessorPool.QueueWorkItem(delegate
                {
                    if (mostRecentItem != null && !mostRecentItem.IsCompleted)
                    {
                        Log.WriteDebug("Steam", "Waiting for package {0} to finish processing (unknown)", workaround);

                        SmartThreadPool.WaitAll(new IWaitableResult[] { mostRecentItem });
                    }

                    new SubProcessor(workaround).ProcessUnknown();
                });

                Application.Instance.ProcessedSubs.AddOrUpdate(package, workerItem, (key, oldValue) => workerItem);
            }
        }

        private static void OnProductInfoForIRC(JobManager.IRCRequest request, SteamApps.PICSProductInfoCallback callback)
        {
            if (request.Type == JobManager.IRCRequestType.TYPE_SUB)
            {
                if (!callback.Packages.ContainsKey(request.Target))
                {
                    CommandHandler.ReplyToCommand(request.Command, "Unknown SubID: {0}{1}", Colors.OLIVE, request.Target);

                    return;
                }

                var info = callback.Packages[request.Target];
                var kv = info.KeyValues.Children.FirstOrDefault();
                string name = string.Format("SubID {0}", info.ID);

                if (kv["name"].Value != null)
                {
                    name = kv["name"].AsString();
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

                CommandHandler.ReplyToCommand(request.Command, "Dump for {0}{1}{2} -{3} {4}{5}{6}{7}",
                    Colors.OLIVE, name, Colors.NORMAL,
                    Colors.DARKBLUE, SteamDB.GetRawPackageURL(info.ID), Colors.NORMAL,
                    info.MissingToken ? SteamDB.StringNeedToken : string.Empty,
                    Application.Instance.OwnedPackages.ContainsKey(info.ID) ? SteamDB.StringCheckmark : string.Empty
                );
            }
            else if (request.Type == JobManager.IRCRequestType.TYPE_APP)
            {
                if (!callback.Apps.ContainsKey(request.Target))
                {
                    CommandHandler.ReplyToCommand(request.Command, "Unknown AppID: {0}{1}", Colors.OLIVE, request.Target);

                    return;
                }

                var info = callback.Apps[request.Target];
                string name = string.Format("AppID {0}", info.ID);

                if (info.KeyValues["common"]["name"].Value != null)
                {
                    name = info.KeyValues["common"]["name"].AsString();
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

                CommandHandler.ReplyToCommand(request.Command, "Dump for {0}{1}{2} -{3} {4}{5}{6}{7}",
                    Colors.OLIVE, name, Colors.NORMAL,
                    Colors.DARKBLUE, SteamDB.GetRawAppURL(info.ID), Colors.NORMAL,
                    info.MissingToken ? SteamDB.StringNeedToken : string.Empty,
                    Application.Instance.OwnedApps.ContainsKey(info.ID) ? SteamDB.StringCheckmark : string.Empty
                );
            }
            else
            {
                CommandHandler.ReplyToCommand(request.Command, "I have no idea what happened here!");
            }
        }
    }
}
