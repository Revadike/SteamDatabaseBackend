/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System.Threading.Tasks;
using SteamKit2;

namespace SteamDatabaseBackend
{
    internal class GIDCommand : Command
    {
        public GIDCommand()
        {
            Trigger = "gid";
        }

        public override async Task OnCommand(CommandArguments command)
        {
            await Task.Yield();

            if (command.Message.Length == 0)
            {
                command.Reply($"Usage:{Colors.OLIVE} gid <globalid>");

                return;
            }

            if (!ulong.TryParse(command.Message, out var uGid))
            {
                command.Reply("Invalid GlobalID.");

                return;
            }

            GlobalID gid = uGid;

            command.Reply($"{(ulong)gid} (SeqCount: {Colors.LIGHTGRAY}{gid.SequentialCount}{Colors.NORMAL}, StartTime: {Colors.LIGHTGRAY}{gid.StartTime}{Colors.NORMAL}, ProcessID: {Colors.LIGHTGRAY}{gid.ProcessID}{Colors.NORMAL}, BoxID: {Colors.LIGHTGRAY}{gid.BoxID}{Colors.NORMAL})");
        }
    }
}
