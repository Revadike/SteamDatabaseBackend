/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Timer = System.Timers.Timer;

namespace SteamDatabaseBackend
{
    static class Application
    {
        public static readonly List<Thread> Threads;

        public static Timer ChangelistTimer { get; private set; }

        public static Dictionary<uint, List<string>> ImportantApps { get; private set; }
        public static Dictionary<uint, byte> ImportantSubs { get; private set; }

        static Application()
        {
            ImportantApps = new Dictionary<uint, List<string>>();
            ImportantSubs = new Dictionary<uint, byte>();

            Threads = new List<Thread>();

            ChangelistTimer = new Timer();
            ChangelistTimer.Elapsed += Tick;
            ChangelistTimer.Interval = TimeSpan.FromSeconds(1).TotalMilliseconds;
        }

        public static void Init()
        {
            ReloadImportant();

            var thread = new Thread(new ThreadStart(Steam.Instance.Tick));
            thread.Name = "Steam";
            thread.Start();

            Threads.Add(thread);

            if (Settings.IsFullRun)
            {
                return;
            }

            var commandHandler = new CommandHandler();

            Steam.Instance.RegisterCommandHandlers(commandHandler);

            if (Settings.Current.IRC.Enabled)
            {
                thread = new Thread(new ThreadStart(IRC.Instance.Connect));
                thread.Name = "IRC";
                thread.Start();

                Threads.Add(thread);

                IRC.Instance.RegisterCommandHandlers(commandHandler);
            }
        }

        private static void Tick(object sender, System.Timers.ElapsedEventArgs e)
        {
            Steam.Instance.Apps.PICSGetChangesSince(Steam.Instance.PICSChanges.PreviousChangeNumber, true, true);
        }

        public static void ReloadImportant(CommandArguments command)
        {
            ReloadImportant();

            CommandHandler.ReplyToCommand(command, "Reloaded {0} important apps and {1} packages", ImportantApps.Count, ImportantSubs.Count);
        }

        private static void ReloadImportant()
        {
            List<Important> importantApps;

            using (var db = Database.GetConnection())
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
    }
}
