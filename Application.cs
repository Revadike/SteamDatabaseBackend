/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;

namespace SteamDatabaseBackend
{
    internal static class Application
    {
        private static Thread IrcThread;
        private static RSS RssReader;

        public static Dictionary<uint, List<string>> ImportantApps { get; private set; }
        public static Dictionary<uint, byte> ImportantSubs { get; private set; }

        public static string Path { get; }

        static Application()
        {
            Path = System.IO.Path.GetDirectoryName(System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName);
        }

        public static async Task Init()
        {
            ImportantApps = new Dictionary<uint, List<string>>();
            ImportantSubs = new Dictionary<uint, byte>();

            await ReloadImportant();
            await KeyNameCache.Init();

            if (Settings.IsFullRun)
            {
                return;
            }

            var commandHandler = new CommandHandler();

            Steam.Instance.RegisterCommandHandlers(commandHandler);

            if (Settings.Current.IRC.Enabled)
            {
                RssReader = new RSS();

                IrcThread = new Thread(IRC.Instance.Connect)
                {
                    Name = "IRC"
                };
                IrcThread.Start();

                IRC.Instance.RegisterCommandHandlers(commandHandler);
            }
        }

        public static async Task ReloadImportant(CommandArguments command)
        {
            await ReloadImportant();

            command.Notice($"Reloaded {ImportantApps.Count} important apps and {ImportantSubs.Count} packages");
        }

        private static async Task ReloadImportant()
        {
            await using var db = await Database.GetConnectionAsync();
            var importantApps = (await db.QueryAsync<Important>("SELECT `AppID` as `ID`, `Channel` FROM `ImportantApps`")).ToList();
            ImportantSubs = (await db.QueryAsync<Important>("SELECT `SubID` as `ID` FROM `ImportantSubs`")).ToDictionary(x => x.ID, _ => (byte)1);

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

            Log.WriteInfo(nameof(Application), $"Loaded {ImportantApps.Count} important apps and {ImportantSubs.Count} packages");
        }

        public static void Cleanup()
        {
            Log.WriteInfo(nameof(Application), "Exiting...");

            try
            {
                Steam.Instance.IsRunning = false;
                Steam.Instance.Client.Disconnect();
                Steam.Instance.Dispose();
            }
            catch (Exception e)
            {
                ErrorReporter.Notify(nameof(Application), e);
            }

            if (Settings.Current.IRC.Enabled)
            {
                Log.WriteInfo(nameof(Application), "Closing IRC connection...");

                RssReader.Timer.Stop();
                RssReader.Dispose();

                IRC.Instance.Close();

                IrcThread.Join(TimeSpan.FromSeconds(5));
            }

            TaskManager.CancelAllTasks();

            LocalConfig.Save();
        }
    }
}
