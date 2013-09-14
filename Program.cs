/*
 * Copyright (c) 2013, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 * 
 * Future non-SteamKit stuff should go in this file.
 */
using System;
using System.Reflection;
using System.Threading;
using Meebey.SmartIrc4net;
using SteamKit2;

namespace SteamDatabaseBackend
{
    public class Program
    {
        public static IrcClient irc = new IrcClient();
        public static Steam steam = new Steam();
        public static SteamDota steamDota = new SteamDota();
        public static SteamProxy ircSteam = new SteamProxy();

        public static void Main(string[] args)
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            string date = new DateTime(2000, 01, 01).AddDays(version.Build).AddSeconds(version.Revision * 2).ToUniversalTime().ToString();

            Log.WriteInfo("Main", "Built on {0} UTC", date);

            try
            {
                Settings.Load();
            }
            catch (Exception e)
            {
                Log.WriteError("Settings", "{0}", e.Message);

                return;
            }

            if (Settings.Current.SteamKitDebug)
            {
                DebugLog.AddListener(new Log.SteamKitLogger());
                DebugLog.Enabled = true;
            }

            Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e)
            {
                Log.WriteInfo("Main", "Exiting...");

                steam.isRunning = false;
                steamDota.isRunning = false;

                try { steam.timer.Stop();                       } catch (Exception) { }
                try { steam.secondaryPool.Shutdown(true, 1000); } catch (Exception) { }
                try { steam.processorPool.Shutdown(true, 1000); } catch (Exception) { }
                try { steam.steamClient.Disconnect();           } catch (Exception) { }

                if (steamDota.steamClient != null)
                {
                    try { steamDota.steamClient.Disconnect(); } catch (Exception) { }
                }

                KillIRC();
            };

            if (Settings.Current.FullRun > 0)
            {
                RunSteam();

                return;
            }

            if (!Settings.CanConnectToIRC())
            {
                RunDoto();
                RunSteam();

                return;
            }

            ircSteam.Run();

            irc.Encoding = System.Text.Encoding.UTF8;
            irc.SendDelay = 1000;
            irc.AutoRetry = true;
            irc.AutoRejoin = true;
            irc.AutoRelogin = true;
            irc.AutoReconnect = true;
            irc.AutoRejoinOnKick = true;
            irc.ActiveChannelSyncing = true;
            irc.OnChannelMessage += new IrcEventHandler(CommandHandler.OnChannelMessage);
            irc.OnConnected += CommandHandler.OnConnected;

            try
            {
                irc.Connect(Settings.Current.IRC.Servers, Settings.Current.IRC.Port);
                irc.Login(Settings.Current.IRC.Nickname, string.Format("built on {0} UTC", date), 4, Settings.Current.IRC.Nickname);
                irc.RfcJoin(new string[] { Settings.Current.IRC.Channel.Main, Settings.Current.IRC.Channel.Announce });

                RunDoto();
                RunSteam();

                irc.Listen();

                KillIRC();
            }
            catch (Exception e)
            {
                Log.WriteError("Main", "Exception: {0}", e.Message);
                Log.WriteError("Main", "Stacktrace: {0}", e.StackTrace);
            }
        }

        private static void RunSteam()
        {
            new Thread(new ThreadStart(steam.Run)).Start();
        }

        private static void RunDoto()
        {
            if (Settings.CanUseDota())
            {
                new Thread(new ThreadStart(steamDota.Run)).Start();
            }
        }

        private static void KillIRC()
        {
            try
            {
                irc.RfcQuit("Exiting, will be back shortly!", Priority.Critical);
                irc.Disconnect();
            }
            catch (Exception) { }
        }
    }
}
