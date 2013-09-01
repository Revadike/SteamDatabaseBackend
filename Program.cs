/*
 * Copyright (c) 2013, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 * 
 * Future non-SteamKit stuff should go in this file.
 */
using System;
using System.Configuration;
using System.Threading;
using Meebey.SmartIrc4net;
using SteamKit2;

namespace PICSUpdater
{
    class Program
    {
        public static IrcClient irc = new IrcClient();
        public static Steam steam = new Steam();
        public static SteamDota steamDota = new SteamDota();
        public static SteamProxy ircSteam = new SteamProxy();
        public static string channelMain = ConfigurationManager.AppSettings["main-channel"];
        public static string channelAnnounce = ConfigurationManager.AppSettings["announce-channel"];

        public static uint fullRunOption;

        static void Main(string[] args)
        {
            if (ConfigurationManager.AppSettings["steam-username"] == null || ConfigurationManager.AppSettings["steam-password"] == null)
            {
                Log.WriteError("Main", "Is config missing? It should be in " + ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None).FilePath);
                return;
            }

            uint.TryParse(ConfigurationManager.AppSettings["fullrun"], out fullRunOption);

            if (ConfigurationManager.AppSettings["steamKitDebug"].Equals("1"))
            {
                DebugLog.AddListener(new SteamKitLogger());
                DebugLog.Enabled = true;
            }

            Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e)
            {
                Log.WriteInfo("Main", "Exiting...");

                steam.isRunning = false;
                steamDota.isRunning = false;

                try { steam.timer.Stop();                 } catch( Exception e2 ) { Log.WriteError("Main", "Exception: {0}", e2.Message); }
                try { steam.steamClient.Disconnect();     } catch( Exception e3 ) { Log.WriteError("Main", "Exception: {0}", e3.Message); }
                try { steamDota.steamClient.Disconnect(); } catch( Exception e4 ) { Log.WriteError("Main", "Exception: {0}", e4.Message); }

                KillIRC();
            };

            if (fullRunOption > 0)
            {
                Log.WriteInfo("Main", "Running full update with option \"{0}\"", fullRunOption);

                RunSteam();

                return;
            }

            if (ConfigurationManager.AppSettings["irc-server"].Length == 0 || ConfigurationManager.AppSettings["irc-port"].Length == 0)
            {
                Log.WriteInfo("Main", "Starting without IRC bot");

                RunDoto();
                RunSteam();

                return;
            }

            irc.Encoding = System.Text.Encoding.UTF8;
            irc.SendDelay = 1000;
            irc.AutoRetry = true;
            irc.AutoRejoin = true;
            irc.AutoRelogin = true;
            irc.AutoReconnect = true;
            irc.AutoRejoinOnKick = true;
            irc.ActiveChannelSyncing = true;
            irc.OnChannelMessage += new IrcEventHandler(CommandHandler.OnChannelMessage);

            string[] serverList = { ConfigurationManager.AppSettings["irc-server"] };
            string[] channels = { channelAnnounce, channelMain };

            try
            {
                irc.Connect(serverList, int.Parse(ConfigurationManager.AppSettings["irc-port"]));
                irc.Login("SteamDB", "http://steamdb.info/", 0, "SteamDB");
                irc.RfcJoin(channels);

                RunDoto();
                RunSteam();

                irc.Listen();

                KillIRC();
            }
            catch(Exception e)
            {
                Log.WriteError("Main", "Exception: {0}", e.Message);
                Log.WriteError("Main", "Stacktrace: {0}", e.StackTrace);
            }
        }

        static void RunSteam()
        {
#if DEBUG
            steam.Run();
#else
            new Thread(new ThreadStart(steam.Run)).Start();
#endif
        }

        static void RunDoto()
        {
            if (ConfigurationManager.AppSettings["steam2-username"].Length > 0 && ConfigurationManager.AppSettings["steam2-password"].Length > 0)
            {
                new Thread(new ThreadStart(steamDota.Run)).Start();
            }
        }

        static void KillIRC()
        {
            try
            {
                irc.RfcQuit("Exiting, will be back shortly!", Priority.Critical);
                irc.Disconnect();
            }
            catch(Exception e)
            {
                Log.WriteError("Main", "Exception while exiting: {0}", e.Message);
            }
        }
    }
}
