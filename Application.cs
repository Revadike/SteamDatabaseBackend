/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Dapper;

namespace SteamDatabaseBackend
{
    internal static class Application
    {
        private static List<Thread> Threads;

        private static RSS RssReader;

        public static Dictionary<uint, List<string>> ImportantApps { get; private set; }
        public static Dictionary<uint, byte> ImportantSubs { get; private set; }

        public static string Path { get; }

        static Application()
        {
            Path = System.IO.Path.GetDirectoryName(typeof(Bootstrapper).Assembly.Location);
        }

        public static void Init()
        {
            ImportantApps = new Dictionary<uint, List<string>>();
            ImportantSubs = new Dictionary<uint, byte>();

            ReloadImportant();
            TaskManager.RunAsync(async () => await KeyNameCache.Init());

            var thread = new Thread(Steam.Instance.Tick)
            {
                Name = "Steam"
            };
            thread.Start();

            Threads = new List<Thread>
            {
                thread,
            };

            if (Settings.IsFullRun)
            {
                return;
            }

            var commandHandler = new CommandHandler();

            Steam.Instance.RegisterCommandHandlers(commandHandler);

            if (Settings.Current.IRC.Enabled)
            {
                RssReader = new RSS();

                thread = new Thread(IRC.Instance.Connect)
                {
                    Name = "IRC"
                };
                thread.Start();

                Threads.Add(thread);

                IRC.Instance.RegisterCommandHandlers(commandHandler);
            }
        }

        public static void ReloadImportant(CommandArguments command)
        {
            ReloadImportant();

            command.Notice("Reloaded {0} important apps and {1} packages", ImportantApps.Count, ImportantSubs.Count);
        }

        private static void ReloadImportant()
        {
            List<Important> importantApps;

            using (var db = Database.Get())
            {
                importantApps = db.Query<Important>("SELECT `AppID` as `ID`, `Channel` FROM `ImportantApps`").ToList();
                ImportantSubs = db.Query<Important>("SELECT `SubID` as `ID` FROM `ImportantSubs`").ToDictionary(x => x.ID, _ => (byte)1);
            }

            lock (ImportantApps)
            {
                ImportantApps.Clear();

                foreach (var app in importantApps)
                {
                    if (ImportantApps.ContainsKey(app.ID))
                    {
                        ImportantApps[app.ID].Add(app.Channel);
                    }
                    else
                    {
                        ImportantApps.Add(app.ID, new List<string> { app.Channel });
                    }
                }
            }

            Log.WriteInfo("Application", "Loaded {0} important apps and {1} packages", ImportantApps.Count, ImportantSubs.Count);
        }

        public static void Cleanup()
        {
            // If threads is null, app was not yet initialized and there is nothing to cleanup
            if (Threads == null)
            {
                return;
            }

            Log.WriteInfo("Bootstrapper", "Exiting...");

            try
            {
                Steam.Instance.IsRunning = false;
                Steam.Instance.Client.Disconnect();
                Steam.Instance.Dispose();
            }
            catch (Exception e)
            {
                ErrorReporter.Notify("Bootstrapper", e);
            }

            if (Settings.Current.IRC.Enabled)
            {
                Log.WriteInfo("Bootstrapper", "Closing IRC connection...");

                RssReader.Timer.Stop();
                RssReader.Dispose();

                IRC.Instance.Close();
            }

            Log.WriteInfo("Bootstrapper", "Cancelling {0} tasks...", TaskManager.TasksCount);

            TaskManager.CancelAllTasks();

            foreach (var thread in Threads.Where(thread => thread.ThreadState == ThreadState.Running))
            {
                Log.WriteInfo("Bootstrapper", "Joining thread {0}...", thread.Name);

                thread.Join(TimeSpan.FromSeconds(5));
            }

            Log.WriteInfo("Bootstrapper", "Saving local config...");

            LocalConfig.Save();
        }
    }
}
