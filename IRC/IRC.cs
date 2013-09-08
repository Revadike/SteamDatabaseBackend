/*
 * Copyright (c) 2013, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using Meebey.SmartIrc4net;

namespace SteamDatabaseBackend
{
    public static class IRC
    {
        public static void Send(string channel, string format, params object[] args)
        {
            Program.irc.SendMessage(SendType.Message, channel, string.Format(format, args));
        }

        public static void SendAnnounce(string format, params object[] args)
        {
            Program.irc.SendMessage(SendType.Message, Settings.Current.IRC.Channel.Announce, string.Format(format, args));
        }

        public static void SendMain(string format, params object[] args)
        {
            Program.irc.SendMessage(SendType.Message, Settings.Current.IRC.Channel.Main, string.Format(format, args));
        }

        public static void SendEmoteAnnounce(string format, params object[] args)
        {
            Program.irc.SendMessage(SendType.Action, Settings.Current.IRC.Channel.Announce, string.Format(format, args));
        }
    }
}
