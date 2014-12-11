/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using NetIrc2;
using SteamKit2;

namespace SteamDatabaseBackend
{
    enum ECommandType
    {
        IRC,
        SteamChatRoom,
        SteamIndividual
    }

    abstract class Command
    {
        public string Trigger { get; protected set; }
        public string Usage { get; protected set; }

        public bool IsAdminCommand { get; protected set; }
        public bool IsSteamCommand { get; protected set; }

        public abstract void OnCommand(CommandArguments command);
    }

    class CommandArguments
    {
        public string Message { get; set; }
        public string Recipient { get; set; }
        public IrcIdentity SenderIdentity { get; set; }
        public ECommandType CommandType { get; set; }
        public SteamID ChatRoomID { get; set; }
        public SteamID SenderID { get; set; }
    }
}
