/*
 * Copyright (c) 2013, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 * 
 * Future non-SteamKit stuff should go in this file.
 */
using System;
using System.Threading;
using Meebey.SmartIrc4net;
using SteamKit2;

namespace PICSUpdater
{
    public class Program
    {
        public static IrcClient irc = new IrcClient();
        public static Steam steam = new Steam();
        public static SteamDota steamDota = new SteamDota();
        public static SteamProxy ircSteam = new SteamProxy();

        public static void Main(string[] args)
        {
            try
            {
                Settings.Load();
            }
            catch (Exception e)
            {
                Log.WriteError("Settings", "{0}", e.Message);

                return;
            }

            if (!Settings.Validate())
            {
                return;
            }

            if (Settings.Current.SteamKitDebug)
            {
                DebugLog.AddListener(new SteamKitLogger());
                DebugLog.Enabled = true;
            }

            Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e)
            {
                Log.WriteInfo("Main", "Exiting...");

                steam.isRunning = false;
                steamDota.isRunning = false;

                try { steam.timer.Stop();                 } catch (Exception e2) { Log.WriteError("Main", "Exception: {0}", e2.Message); }
                try { steam.steamClient.Disconnect();     } catch (Exception e3) { Log.WriteError("Main", "Exception: {0}", e3.Message); }
                try { steamDota.steamClient.Disconnect(); } catch (Exception e4) { Log.WriteError("Main", "Exception: {0}", e4.Message); }

                KillIRC();
            };

            if (Settings.Current.FullRun > 0)
            {
                Log.WriteInfo("Main", "Running full update with option \"{0}\"", Settings.Current.FullRun);

                RunSteam();

                return;
            }

            if (!Settings.CanConnectToIRC())
            {
                RunDoto();
                RunSteam();

                return;
            }

            ircSteam.ReloadImportant();

            irc.Encoding = System.Text.Encoding.UTF8;
            irc.SendDelay = 1000;
            irc.AutoRetry = true;
            irc.AutoRejoin = true;
            irc.AutoRelogin = true;
            irc.AutoReconnect = true;
            irc.AutoRejoinOnKick = true;
            irc.ActiveChannelSyncing = true;
            irc.OnChannelMessage += new IrcEventHandler(CommandHandler.OnChannelMessage);

            //string[] serverList = { ConfigurationManager.AppSettings["irc-server"] };
            string[] channels = { Settings.Current.IRC.Channel.Main, Settings.Current.IRC.Channel.Announce };

            try
            {
                irc.Connect(Settings.Current.IRC.Server, Settings.Current.IRC.Port);
                irc.Login(Settings.Current.IRC.Nickname, "http://steamdb.info/", 0, Settings.Current.IRC.Nickname);
                irc.RfcJoin(channels);

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
#if DEBUG
            steam.Run();
#else
            new Thread(new ThreadStart(steam.Run)).Start();
#endif
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
            catch (Exception e)
            {
                Log.WriteError("Main", "Exception while exiting: {0}", e.Message);
            }
        }
    }
}
