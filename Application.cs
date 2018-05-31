/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Timers;
using Dapper;
using Timer = System.Timers.Timer;

namespace SteamDatabaseBackend
{
    static class Application
    {
        private static List<Thread> Threads;

        private static RSS RssReader;

        public static Timer ChangelistTimer { get; private set; }

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

            ChangelistTimer = new Timer();
            ChangelistTimer.Elapsed += Tick;
            ChangelistTimer.Interval = TimeSpan.FromSeconds(1).TotalMilliseconds;

            var thread = new Thread(Steam.Instance.Tick)
            {
                Name = "Steam"
            };
            thread.Start();

            var anonThread = new Thread(Steam.Anonymous.Tick)
            {
                Name = "SteamAnonymous"
            };
            anonThread.Start();

            Threads = new List<Thread>
            {
                thread,
                anonThread,
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

        private static void Tick(object sender, ElapsedEventArgs e)
        {
            Steam.Instance.Apps.PICSGetChangesSince(Steam.Instance.PICSChanges.PreviousChangeNumber, true, true);
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
                ImportantSubs = db.Query<Important>("SELECT `SubID` as `ID` FROM `ImportantSubs`").ToDictionary(x => x.ID, x => (byte)1);
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
                        ImportantApps.Add(app.ID, new List<string>{ app.Channel });
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

            ChangelistTimer.Stop();

            Log.WriteInfo("Bootstrapper", "Disconnecting from Steam...");

            try
            {
                Steam.Instance.IsRunning = false;
                Steam.Instance.Client.Disconnect();
            }
            catch (Exception e)
            {
                ErrorReporter.Notify("Bootstrapper", e);
            }

            if (Settings.Current.IRC.Enabled)
            {
                Log.WriteInfo("Bootstrapper", "Closing IRC connection...");

                RssReader.Timer.Stop();

                IRC.Instance.Close();
            }

            Log.WriteInfo("Bootstrapper", "Cancelling {0} tasks...", TaskManager.TasksCount);

            TaskManager.CancelAllTasks();

            foreach (var thread in Threads.Where(thread => thread.ThreadState == ThreadState.Running))
            {
                Log.WriteInfo("Bootstrapper", "Joining thread {0}...", thread.Name);

                thread.Join(TimeSpan.FromSeconds(5));
            }

            Log.WriteInfo("Bootstrapper", "Truncating GC table...");

            using (var db = Database.Get())
            {
                db.Execute("DELETE FROM `GC`");
            }

            Log.WriteInfo("Bootstrapper", "Saving local config...");

            LocalConfig.Save();

            try
            {
                Steam.Anonymous.IsRunning = false;
                Steam.Anonymous.Client.Disconnect();
            }
            catch (Exception e)
            {
                ErrorReporter.Notify("Bootstrapper", e);
            }
        }
    }
}
