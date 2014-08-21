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
    class IRC
    {
        private static IRC _instance = new IRC();
        public static IRC Instance { get { return _instance; } }

        private IrcClient Client;

        public void Init()
        {
            Client = new IrcClient();

            Client.OnConnected += OnConnected;
            Client.OnDisconnected += OnDisconnected;

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
                Client.AutoReconnect = false;
                Client.SendMessage(SendType.Action, Settings.Current.IRC.Channel.Main, "is exiting... send help", Priority.Critical);
                Client.RfcQuit("Exit", Priority.Critical);
                Client.Disconnect();
            }
            catch { }
        }

        public void RegisterCommandHandlers(CommandHandler handler)
        {
            Client.OnChannelMessage += handler.OnIRCMessage;
            Client.OnQueryMessage += handler.OnIRCMessage;
        }

        private void OnConnected(object sender, EventArgs e)
        {
            Log.WriteInfo("IRC", "Connected to IRC successfully");
        }

        private void OnDisconnected(object sender, EventArgs e)
        {
            Log.WriteInfo("IRC", "Disconnected from IRC");
        }

        public void SendReply(IrcMessageData data, string message, Priority priority)
        {
            Client.SendReply(data, message, priority);
        }

        public void SendAnnounce(string format, params object[] args)
        {
            if (Settings.Current.IRC.Enabled)
            {
                Client.SendMessage(SendType.Message, Settings.Current.IRC.Channel.Announce, string.Format(format, args));
            }
        }

        public void SendMain(string format, params object[] args)
        {
            if (Settings.Current.IRC.Enabled)
            {
                Client.SendMessage(SendType.Message, Settings.Current.IRC.Channel.Main, string.Format(format, args), Priority.AboveMedium);
            }
        }

        // Woo, hardcoding!
        public void SendSteamLUG(string message)
        {
            if (Settings.Current.IRC.Enabled)
            {
                Client.SendMessage(SendType.Message, "#steamlug", message, Priority.AboveMedium);
            }
        }

        public void SendEmoteAnnounce(string format, params object[] args)
        {
            if (Settings.Current.IRC.Enabled)
            {
                Client.SendMessage(SendType.Action, Settings.Current.IRC.Channel.Announce, string.Format(format, args));
            }
        }

        public bool IsSenderOp(IrcMessageData message)
        {
            ChannelUser user = Client.GetChannelUser(message.Channel, message.Nick);

            return user != null && user.IsOp;
        }
    }
}
