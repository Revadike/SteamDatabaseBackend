/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Text;
using Meebey.SmartIrc4net;

namespace SteamDatabaseBackend
{
    public class IRC
    {
        private static IRC _instance = new IRC();
        public static IRC Instance { get { return _instance; } }

        public IrcClient Client = new IrcClient();

        public void Init()
        {
            Client.OnChannelMessage += CommandHandler.OnIRCMessage;
            Client.OnQueryMessage += CommandHandler.OnIRCMessage;
            Client.OnConnected += OnConnected;

            Client.Encoding = Encoding.UTF8;
            Client.SendDelay = (int)Settings.Current.IRC.SendDelay;
            Client.AutoRetry = true;
            Client.AutoRetryDelay = 15;
            Client.AutoRetryLimit = 0;
            Client.AutoRejoin = true;
            Client.AutoRelogin = true;
            Client.AutoReconnect = true;
            Client.AutoRejoinOnKick = true;
            Client.ActiveChannelSyncing = true;

            try
            {
                Client.Connect(Settings.Current.IRC.Servers, Settings.Current.IRC.Port);
                Client.Login(Settings.Current.IRC.Nickname, Settings.Current.BaseURL.AbsoluteUri, 4, Settings.Current.IRC.Nickname, Settings.Current.IRC.Password);
                Client.RfcJoin(new string[] { Settings.Current.IRC.Channel.Main, Settings.Current.IRC.Channel.Announce });
                Client.Listen();

                Kill();
            }
            catch (Exception e)
            {
                Log.WriteError("IRC", "Exception: {0}\n{1}", e.Message, e.StackTrace);
            }
        }

        public void Kill()
        {
            try
            {
                Instance.Client.AutoReconnect = false;
                Instance.Client.SendMessage(SendType.Action, Settings.Current.IRC.Channel.Main, "is exiting... send help", Priority.Critical);
                Instance.Client.RfcQuit("Exit", Priority.Critical);
                Instance.Client.Disconnect();
            }
            catch { }
        }

        private static void OnConnected(object sender, EventArgs e)
        {
            Log.WriteInfo("IRC Proxy", "Connected to IRC successfully");
        }

        public static void SendAnnounce(string format, params object[] args)
        {
            if (Settings.Current.IRC.Enabled)
            {
                Instance.Client.SendMessage(SendType.Message, Settings.Current.IRC.Channel.Announce, string.Format(format, args));
            }
        }

        public static void SendMain(string format, params object[] args)
        {
            if (Settings.Current.IRC.Enabled)
            {
                Instance.Client.SendMessage(SendType.Message, Settings.Current.IRC.Channel.Main, string.Format(format, args), Priority.AboveMedium);
            }
        }

        // Woo, hardcoding!
        public static void SendSteamLUG(string message)
        {
            if (Settings.Current.IRC.Enabled)
            {
                Instance.Client.SendMessage(SendType.Message, "#steamlug", message, Priority.AboveMedium);
            }
        }

        public static void SendEmoteAnnounce(string format, params object[] args)
        {
            if (Settings.Current.IRC.Enabled)
            {
                Instance.Client.SendMessage(SendType.Action, Settings.Current.IRC.Channel.Announce, string.Format(format, args));
            }
        }

        public static bool IsSenderOp(IrcMessageData message)
        {
            ChannelUser user = Instance.Client.GetChannelUser(message.Channel, message.Nick);

            return user != null && user.IsOp;
        }
    }
}
