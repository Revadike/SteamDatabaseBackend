/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections.Generic;
using System.Linq;

namespace SteamDatabaseBackend
{
    class HelpCommand : Command
    {
        private readonly string Commands;

        public HelpCommand(List<Command> commands)
        {
            Trigger = "!help";

            Commands = string.Join(string.Format("{0}, {1}", Colors.NORMAL, Colors.OLIVE), commands.Select(cmd => cmd.Trigger));
        }

        public override void OnCommand(CommandArguments command)
        {
            if (command.Message.Length > 0)
            {
                return;
            }

            CommandHandler.ReplyToCommand(command, "Available commands: {0}{1}", Colors.OLIVE, Commands);
        }
    }
}
