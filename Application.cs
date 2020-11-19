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
        private static HttpServer HttpServer;

        public static HashSet<uint> ImportantApps { get; private set; }
        public static HashSet<uint> ImportantSubs { get; private set; }

        public static string Path { get; }

        static Application()
        {
            Path = System.IO.Path.GetDirectoryName(System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName);
        }

        public static async Task Init()
        {
            ImportantApps = new HashSet<uint>();
            ImportantSubs = new HashSet<uint>();

            await ReloadImportant();
            await PICSTokens.Reload();
            await KeyNameCache.Init();

            if (Settings.Current.BuiltInHttpServerPort > 0)
            {
                HttpServer = new HttpServer(Settings.Current.BuiltInHttpServerPort);
            }

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

        public static async Task ReloadImportant()
        {
            await using var db = await Database.GetConnectionAsync();

            ImportantApps = (await db.QueryAsync<uint>("SELECT `AppID` as `ID` FROM `ImportantApps`")).ToHashSet();
            ImportantSubs = (await db.QueryAsync<uint>("SELECT `SubID` as `ID` FROM `ImportantSubs`")).ToHashSet();

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

            if (Settings.Current.BuiltInHttpServerPort > 0)
            {
                HttpServer.Dispose();
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

            if (!Settings.IsFullRun)
            {
                LocalConfig.Update("backend.changenumber", Steam.Instance.PICSChanges.PreviousChangeNumber.ToString())
                    .GetAwaiter().GetResult();
            }
        }
    }
}
