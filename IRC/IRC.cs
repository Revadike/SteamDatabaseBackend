/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using NetIrc2;

namespace SteamDatabaseBackend
{
    class IRC
    {
        private static IRC _instance = new IRC();
        public static IRC Instance { get { return _instance; } }

        private readonly IrcClient Client;
        private bool Disconnecting;

        public IRC()
        {
            if (!Settings.Current.IRC.Enabled)
            {
                return;
            }

            Client = new IrcClient();

            Client.ClientVersion = "Steam Database -- https://github.com/SteamDatabase/SteamDatabaseBackend";

            Client.Connected += OnConnected;
            Client.Closed += OnDisconnected;
            Client.GotIrcError += OnError;
        }

        public void Connect()
        {
            try
            {
                Client.Connect(Settings.Current.IRC.Server, Settings.Current.IRC.Port);
            }
            catch (Exception e)
            {
                Log.WriteError("IRC", "Failed to connect: {0}\n{1}", e.Message, e.StackTrace);
            }
        }

        public void Close(bool isCancelKeyExit)
        {
            Disconnecting = true;

            if (!Client.IsConnected)
            {
                return;
            }

            if (!isCancelKeyExit)
            {
                Client.ChatAction(Settings.Current.IRC.Channel.Main, "is exiting…");
            }

            SendEmoteAnnounce("is exiting…");
            Client.LogOut();
            Client.Close();
        }

        public void RegisterCommandHandlers(CommandHandler handler)
        {
            Client.GotMessage += handler.OnIRCMessage;
        }

        private void OnConnected(object sender, EventArgs e)
        {
            Log.WriteInfo("IRC", "Connected to IRC successfully");

            Client.LogIn(Settings.Current.IRC.Nickname, Settings.Current.BaseURL.AbsoluteUri, Settings.Current.IRC.Nickname, "4", null, Settings.Current.IRC.Password);
            Client.Join(Settings.Current.IRC.Channel.Ops);
            Client.Join(Settings.Current.IRC.Channel.Main);
            Client.Join(Settings.Current.IRC.Channel.Announce);
        }

        private void OnDisconnected(object sender, EventArgs e)
        {
            Log.WriteInfo("IRC", "Disconnected from IRC");

            if (!Disconnecting)
            {
                Connect();
            }
        }

        private void OnError(object sender, NetIrc2.Events.IrcErrorEventArgs e)
        {
            switch (e.Error)
            {
                case IrcReplyCode.MissingMOTD:
                    return;
            }

            Log.WriteError("IRC", "Error: {0} ({1})", e.Error.ToString(), string.Join(", ", e.Data.Parameters));
        }

        public void SendReply(string recipient, string message)
        {
            Client.Message(recipient, message); //, Priority.AboveMedium);
        }

        public void SendAnnounce(string format, params object[] args)
        {
            if (Settings.Current.IRC.Enabled)
            {
                Client.Message(Settings.Current.IRC.Channel.Announce, string.Format(format, args));
            }
        }

        public void SendMain(string format, params object[] args)
        {
            if (Settings.Current.IRC.Enabled)
            {
                Client.Message(Settings.Current.IRC.Channel.Main, string.Format(format, args)); //, Priority.AboveMedium);
            }
        }

        public void SendOps(string format, params object[] args)
        {
            if (Settings.Current.IRC.Enabled)
            {
                Client.Message(Settings.Current.IRC.Channel.Ops, string.Format(format, args)); //, Priority.Highest);
            }
        }

        // Woo, hardcoding!
        public void SendSteamLUG(string message)
        {
            if (Settings.Current.IRC.Enabled)
            {
                Client.Message("#steamlug", message); //, Priority.AboveMedium);
            }
        }

        public void SendEmoteAnnounce(string format, params object[] args)
        {
            if (Settings.Current.IRC.Enabled)
            {
                Client.ChatAction(Settings.Current.IRC.Channel.Announce, string.Format(format, args));
            }
        }

        public void AnnounceImportantAppUpdate(uint appID, string format, params object[] args)
        {
            if (!Settings.Current.IRC.Enabled)
            {
                return;
            }

            List<string> channels;

            if (Application.ImportantApps.TryGetValue(appID, out channels))
            {
                format = string.Format(format, args);

                foreach (var channel in channels)
                {
                    Client.Message(channel, format); //, Priority.AboveMedium);
                }
            }
        }

        private static readonly char[] ChannelCharacters = { '#', '!', '+', '&' };

        public static bool IsRecipientChannel(string recipient)
        {
            return ChannelCharacters.Contains(recipient[0]);
        }
    }
}
