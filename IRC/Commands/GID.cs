/*
 * Copyright (c) 2013-2018, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System.Threading.Tasks;
using SteamKit2;

namespace SteamDatabaseBackend
{
    class GIDCommand : Command
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
                command.Reply("Usage:{0} gid <globalid>", Colors.OLIVE);

                return;
            }

            if (!ulong.TryParse(command.Message, out var uGid))
            {
                command.Reply("Invalid GlobalID.");

                return;
            }

            GlobalID gid = uGid;

            command.Reply("{0} (SeqCount: {1}{2}{3}, StartTime: {4}{5}{6}, ProcessID: {7}{8}{9}, BoxID: {10}{11}{12})",
                (ulong)gid,
                Colors.LIGHTGRAY, gid.SequentialCount, Colors.NORMAL,
                Colors.LIGHTGRAY, gid.StartTime, Colors.NORMAL,
                Colors.LIGHTGRAY, gid.ProcessID, Colors.NORMAL,
                Colors.LIGHTGRAY, gid.BoxID, Colors.NORMAL
            );
        }
    }
}
