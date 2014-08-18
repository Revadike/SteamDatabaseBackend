/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using Meebey.SmartIrc4net;
using SteamKit2;

namespace SteamDatabaseBackend
{
    public abstract class Command
    {
        public string Trigger { get; protected set; }
        public string Usage { get; protected set; }

        public bool IsAdminCommand { get; protected set; }
        public bool IsSteamCommand { get; protected set; }

        public abstract void OnCommand(CommandArguments command);
    }

    public class CommandArguments
    {
        public string Message { get; set; }
        public IrcMessageData MessageData { get; set; }
        public SteamID ChatRoomID { get; set; }
        public SteamID SenderID { get; set; }

        public bool IsChatRoomCommand
        {
            get
            {
                return this.ChatRoomID != null;
            }
        }
    }
}
