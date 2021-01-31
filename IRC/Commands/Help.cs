/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SteamDatabaseBackend
{
    internal class HelpCommand : Command
    {
        private readonly List<Command> Commands;

        public HelpCommand(List<Command> commands)
        {
            Trigger = "help";

            Commands = commands;
        }

        public override async Task OnCommand(CommandArguments command)
        {
            await Task.Yield();

            if (command.Message.Length > 0)
            {
                return;
            }

            var commands = Commands
                .Where(cmd => cmd != this)
                .Select(cmd => cmd.Trigger);

            command.Notice($"Available commands: {Colors.OLIVE}{string.Join($"{Colors.NORMAL}, {Colors.OLIVE}", commands)}");
        }
    }
}
