/*
 * Copyright (c) 2013, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using MySql.Data.MySqlClient;

namespace SteamDatabaseBackend
{
    class ReloginCommand : Command
    {
        public ReloginCommand()
        {
            Trigger = "!relogin";
            IsAdminCommand = true;
        }

        public override void OnCommand(CommandArguments command)
        {
            if (Steam.Instance.Client.IsConnected)
            {
                Steam.Instance.Client.Connect();
            }

            foreach (var idler in Program.GCIdlers)
            {
                if (idler.Client.IsConnected)
                {
                    idler.Client.Connect();
                }
            }

            CommandHandler.ReplyToCommand(command, "Reconnect forced.");
        }
    }
}
