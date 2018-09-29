/*
 * Copyright (c) 2013-2018, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using NetIrc2;
using NetIrc2.Events;

namespace SteamDatabaseBackend
{
    class IRC
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

                    if (Settings.Current.IRC.SslAcceptInvalid)
                    {
                        options.SslCertificateValidationCallback = (sender, certificate, chain, policyErrors) => true;
                    }
                }

                Client.Connect(Settings.Current.IRC.Server, Settings.Current.IRC.Port, options);
            }
            catch (Exception e)
            {
                ErrorReporter.Notify("IRC", e);
            }
        }

        public void Close()
        {
            Disconnecting = true;

            if (!Client.IsConnected)
            {
                return;
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

        private void OnError(object sender, IrcErrorEventArgs e)
        {
            switch (e.Error)
            {
                case IrcReplyCode.MissingMOTD:
                    return;
            }

            Log.WriteError("IRC", "Error: {0} ({1})", e.Error.ToString(), string.Join(", ", e.Data.Parameters));
        }

        public void SendReply(string recipient, string message, bool notice)
        {
            if (notice)
            {
                // Reset formatting since some clients might put notices in a different color
                message = string.Format("{0}{1}{2}", Settings.Current.IRC.PrioritySendPrefix, Colors.NORMAL, message);

                Client.Notice(recipient, message);
            }
            else
            {
                Client.Message(recipient, string.Concat(Settings.Current.IRC.PrioritySendPrefix, message));
            }
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
                Client.Message(Settings.Current.IRC.Channel.Main, string.Concat(Settings.Current.IRC.PrioritySendPrefix, string.Format(format, args)));
            }
        }

        public void SendOps(string format, params object[] args)
        {
            if (Settings.Current.IRC.Enabled)
            {
                Client.Message(Settings.Current.IRC.Channel.Ops, string.Concat(Settings.Current.IRC.PrioritySendPrefix, string.Format(format, args)));
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

            if (!Application.ImportantApps.TryGetValue(appID, out var channels))
            {
                return;
            }

            format = string.Concat(Settings.Current.IRC.PrioritySendPrefix, string.Format(format, args));

            foreach (var channel in channels)
            {
                Client.Message(channel, format); //, Priority.AboveMedium);
            }
        }

        public void AnnounceImportantPackageUpdate(uint subID, string format, params object[] args)
        {
            SendMain(format, args); // TODO
        }

        private static readonly char[] ChannelCharacters = { '#', '!', '+', '&' };

        public static bool IsRecipientChannel(string recipient)
        {
            return ChannelCharacters.Contains(recipient[0]);
        }
    }
}
