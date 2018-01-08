/*
 * Copyright (c) 2013-2018, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System.Threading.Tasks;

namespace SteamDatabaseBackend
{
    class ReloginCommand : Command
    {
        public ReloginCommand()
        {
            Trigger = "relogin";
            IsAdminCommand = true;
        }

        public override async Task OnCommand(CommandArguments command)
        {
            await Task.Yield();

            Steam.Instance.Client.Connect();
        }
    }
}
