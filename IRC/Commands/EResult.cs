/*
 * Copyright (c) 2013, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using SteamKit2;

namespace SteamDatabaseBackend
{
    class EResultCommand : Command
    {
        public EResultCommand()
        {
            Trigger = "!eresult";
        }

        public override void OnCommand(CommandArguments command)
        {
            if (command.Message.Length == 0)
            {
                CommandHandler.ReplyToCommand(command, "Usage:{0} !eresult <number>", Colors.OLIVE);

                return;
            }

            if (command.Message.Equals("consistency", StringComparison.CurrentCultureIgnoreCase))
            {
                CommandHandler.ReplyToCommand(command, "{0}Consistency{1} = {2}Valve", Colors.LIGHT_GRAY, Colors.NORMAL, Colors.RED);

                return;
            }

            int eResult;

            if (!int.TryParse(command.Message, out eResult))
            {
                try
                {
                    eResult = (int)Enum.Parse(typeof(EResult), command.Message, true);
                }
                catch
                {
                    eResult = -1;
                }
            }

            if(!Enum.IsDefined(typeof(EResult), eResult))
            {
                CommandHandler.ReplyToCommand(command, "Unknown or invalid EResult.");

                return;
            }

            CommandHandler.ReplyToCommand(command, "{0}{1}{2} = {3}", Colors.LIGHT_GRAY, eResult, Colors.NORMAL, (EResult)eResult);
        }
    }
}
