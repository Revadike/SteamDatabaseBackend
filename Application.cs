/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Timer = System.Timers.Timer;

namespace SteamDatabaseBackend
{
    static class Application
    {
        public static readonly List<Thread> Threads;

        public static Timer ChangelistTimer { get; private set; }

        public static Dictionary<uint, byte> OwnedApps { get; set; }
        public static Dictionary<uint, byte> OwnedSubs { get; set; }

        public static Dictionary<uint, List<string>> ImportantApps { get; private set; }
        public static Dictionary<uint, byte> ImportantSubs { get; private set; }

        public static ConcurrentDictionary<uint, Task> ProcessedApps { get; private set; }
        public static ConcurrentDictionary<uint, Task> ProcessedSubs { get; private set; }

        static Application()
        {
            OwnedApps = new Dictionary<uint, byte>();
            OwnedSubs = new Dictionary<uint, byte>();

            ImportantApps = new Dictionary<uint, List<string>>();
            ImportantSubs = new Dictionary<uint, byte>();

            ProcessedApps = new ConcurrentDictionary<uint, Task>();
            ProcessedSubs = new ConcurrentDictionary<uint, Task>();

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
            using (var reader = DbWorker.ExecuteReader("SELECT `AppID`, `Channel` FROM `ImportantApps`"))
            {
                ImportantApps.Clear();

                while (reader.Read())
                {
                    var appID = reader.GetUInt32("AppID");
                    var channel = reader.GetString("Channel");

                    if (ImportantApps.ContainsKey(appID))
                    {
                        ImportantApps[appID].Add(channel);
                    }
                    else
                    {
                        ImportantApps.Add(appID, new List<string>{ channel });
                    }
                }
            }

            using (var reader = DbWorker.ExecuteReader("SELECT `SubID` FROM `ImportantSubs`"))
            {
                ImportantSubs.Clear();

                while (reader.Read())
                {
                    ImportantSubs.Add(reader.GetUInt32("SubID"), 1);
                }
            }

            Log.WriteInfo("Application", "Loaded {0} important apps and {1} packages", ImportantApps.Count, ImportantSubs.Count);
        }
    }
}
