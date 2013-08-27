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

namespace PICSUpdater
{
    class Program
    {
        public static Steam steam = new Steam();
        public static IrcClient irc = new IrcClient();
        public static string channelMain = ConfigurationManager.AppSettings["main-channel"];
        public static string channelAnnounce = ConfigurationManager.AppSettings["announce-channel"];
        private static Thread steamThread;

        static void Main(string[] args)
        {
            if (ConfigurationManager.AppSettings["steam-username"] == null || ConfigurationManager.AppSettings["steam-password"] == null)
            {
                Console.WriteLine("Is config missing? It should be in " + ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None).FilePath);
                return;
            }

            Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e)
            {
                Console.WriteLine("Exiting...");

                steam.isRunning = false;

                steam.timer.Stop();

                steam.steamClient.Disconnect();

                KillIRC();
            };

            irc.Encoding = System.Text.Encoding.UTF8;
            irc.SendDelay = 1500;
            irc.AutoRetry = true;
            irc.AutoRejoin = true;
            irc.AutoRelogin = true;
            irc.AutoReconnect = true;
            irc.AutoRejoinOnKick = true;
            irc.ActiveChannelSyncing = true;
            irc.OnChannelMessage += new IrcEventHandler(CommandHandler.OnChannelMessage);

            string[] serverList = { ConfigurationManager.AppSettings["irc-server"] };

            try
            {
                irc.Connect(serverList, int.Parse(ConfigurationManager.AppSettings["irc-port"]));
                irc.Login("SteamDB", "http://steamdb.info/", 0, "SteamDB");
                irc.RfcJoin(channelAnnounce);

                steamThread = new Thread(new ThreadStart(steam.Run));
                steamThread.Start();

                irc.Listen();

                KillIRC();
            }
            catch(Exception e)
            {
                Console.WriteLine("Exception: {0}", e.Message);
                //Console.WriteLine(e.StackTrace);
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
                Console.WriteLine("Exception while exiting: {0}", e.Message);
            }
        }
    }
}
