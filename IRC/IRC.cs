/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Linq;
using NetIrc2;
using NetIrc2.Events;

namespace SteamDatabaseBackend
{
    internal class IRC
    {
        public static IRC Instance { get; } = new IRC();

        private readonly IrcClient Client;
        private bool Disconnecting;

        public bool IsConnected => Client.IsConnected;

        private IRC()
        {
            if (!Settings.Current.IRC.Enabled)
            {
                return;
            }

            Client = new IrcClient
            {
                ClientVersion = "Steam Database -- https://github.com/SteamDatabase/SteamDatabaseBackend"
            };

            Client.Connected += OnConnected;
            Client.Closed += OnDisconnected;
            Client.GotIrcError += OnError;
        }

        public void Connect()
        {
            try
            {
                IrcClientConnectionOptions options = null;

                if (Settings.Current.IRC.Ssl)
                {
                    options = new IrcClientConnectionOptions
                    {
                        Ssl = true,
                        SslHostname = Settings.Current.IRC.Server
                    };
                }

                Client.Connect(Settings.Current.IRC.Server, Settings.Current.IRC.Port, options);
            }
            catch (Exception e)
            {
                ErrorReporter.Notify(nameof(IRC), e);
            }
        }

        public void Close()
        {
            Disconnecting = true;

            if (!Client.IsConnected)
            {
                return;
            }

            Client.LogOut();
            Client.Close();
        }

        public void RegisterCommandHandlers(CommandHandler handler)
        {
            Client.GotMessage += handler.OnIRCMessage;
        }

        private void OnConnected(object sender, EventArgs e)
        {
            Log.WriteInfo(nameof(IRC), "Connected to IRC successfully");

            Client.LogIn(Settings.Current.IRC.Nickname, Settings.Current.BaseURL.AbsoluteUri, Settings.Current.IRC.Nickname, "4", null, Settings.Current.IRC.Password);
            Client.Join(Settings.Current.IRC.Channel.Ops);
            Client.Join(Settings.Current.IRC.Channel.Announce);
        }

        private void OnDisconnected(object sender, EventArgs e)
        {
            Log.WriteInfo(nameof(IRC), "Disconnected from IRC");

            if (!Disconnecting)
            {
                Client.Close();
                Connect();
            }
        }

        private void OnError(object sender, IrcErrorEventArgs e)
        {
            if (e.Error == IrcReplyCode.MissingMOTD)
            {
                return;
            }

            Log.WriteError(nameof(IRC), $"Error: {e.Error} ({string.Join(", ", e.Data.Parameters)})");
        }

        public void SendReply(string recipient, string message, bool notice)
        {
            if (notice)
            {
                // Reset formatting since some clients might put notices in a different color
                message = $"{Colors.NORMAL}{message}";

                Client.Notice(recipient, message);
            }
            else
            {
                Client.Message(recipient, message);
            }
        }

        public void SendAnnounce(string str)
        {
            if (Settings.Current.IRC.Enabled)
            {
                Client.Message(Settings.Current.IRC.Channel.Announce, str);
            }
        }

        public void SendOps(string str)
        {
            if (Settings.Current.IRC.Enabled)
            {
                Client.Message(Settings.Current.IRC.Channel.Ops, str);
            }
        }

        // Woo, hardcoding!
        public void SendLinuxAnnouncement(string message)
        {
            if (Settings.Current.IRC.Enabled)
            {
                Client.Message("#steamlug", message);
                Client.Message("#gamingonlinux", message);
            }
        }

        private static readonly char[] ChannelCharacters = { '#', '!', '+', '&' };

        public static bool IsRecipientChannel(string recipient)
        {
            return ChannelCharacters.Contains(recipient[0]);
        }
    }
}
