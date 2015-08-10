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
using System.Timers;
using Dapper;
using Timer = System.Timers.Timer;

namespace SteamDatabaseBackend
{
    static class Application
    {
        private static readonly List<Thread> Threads;

        private static RSS RssReader;

        public static Timer ChangelistTimer { get; private set; }

        public static Dictionary<uint, List<string>> ImportantApps { get; private set; }
        public static Dictionary<uint, byte> ImportantSubs { get; private set; }

        public static string Path { get; private set; }

        static Application()
        {
            Path = System.IO.Path.GetDirectoryName(typeof(Bootstrapper).Assembly.Location);

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

            var thread = new Thread(Steam.Instance.Tick);
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
                RssReader = new RSS();

                thread = new Thread(IRC.Instance.Connect);
                thread.Name = "IRC";
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

            command.ReplyAsNotice = true;
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

        public static void Cleanup(bool cleaningUp)
        {
            Log.WriteInfo("Bootstrapper", "Exiting...");

            ChangelistTimer.Stop();

            Steam.Instance.IsRunning = false;

            var count = PICSProductInfo.ProcessedApps.Count;

            if (count > 0)
            {
                Log.WriteInfo("Bootstrapper", "{0} app tasks left, waiting", count);

                Task.WaitAll(PICSProductInfo.ProcessedApps.Values.ToArray());
            }

            count = PICSProductInfo.ProcessedSubs.Count;

            if (count > 0)
            {
                Log.WriteInfo("Bootstrapper", "{0} package tasks left, waiting", count);

                Task.WaitAll(PICSProductInfo.ProcessedSubs.Values.ToArray());
            }

            Log.WriteInfo("Bootstrapper", "Disconnecting from Steam");

            try { Steam.Instance.Client.Disconnect(); } catch { }

            if (Settings.Current.IRC.Enabled)
            {
                Log.WriteInfo("Bootstrapper", "Closing IRC connection");

                RssReader.Timer.Stop();

                IRC.Instance.Close(cleaningUp);
            }

            foreach (var thread in Threads)
            {
                if (thread.ThreadState == ThreadState.Running)
                {
                    Log.WriteInfo("Bootstrapper", "Joining thread {0}", thread.Name);

                    thread.Join(TimeSpan.FromSeconds(30));
                }
            }

            using (var db = Database.GetConnection())
            {
                db.Execute("DELETE FROM `GC`");
            }
        }
    }
}
