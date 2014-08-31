/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
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
            Steam.Instance.Client.Connect();

            foreach (var idler in Application.GCIdlers)
            {
                idler.Client.Connect();
            }

            CommandHandler.ReplyToCommand(command, "Reconnect forced.");
        }
    }
}
