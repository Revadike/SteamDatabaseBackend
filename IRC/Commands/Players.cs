/*
 * Copyright (c) 2013-2018, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Threading.Tasks;
using SteamKit2;

namespace SteamDatabaseBackend
{
    class PlayersCommand : Command
    {
        public PlayersCommand()
        {
            Trigger = "players";
            IsSteamCommand = true;
        }

        public override async Task OnCommand(CommandArguments command)
        {
            if (command.Message.Length == 0)
            {
                command.Reply("Usage:{0} players <appid or partial game name>", Colors.OLIVE);

                return;
            }

            string name;

            if (!uint.TryParse(command.Message, out var appID))
            {
                appID = await AppCommand.TrySearchAppId(command);

                if (appID == 0)
                {
                    command.Reply("Nothing was found matching your request.");

                    return;
                }
            }

            var callback = await Steam.Instance.UserStats.GetNumberOfCurrentPlayers(appID);

            if (appID == 0)
            {
                appID = 753;
            }
            
            name = Steam.GetAppName(appID, out var appType);

            if (callback.Result != EResult.OK)
            {
                command.Reply($"Unable to request player count for {Colors.BLUE}{name}{Colors.NORMAL}: {Colors.RED}{callback.Result}{Colors.NORMAL} -{Colors.DARKBLUE} {SteamDB.GetAppURL(appID, "graphs")}");

                return;
            }

            var type = "playing";

            switch (appType)
            {
                case "Tool":
                case "Config":
                case "Application":
                    type = "using";
                    break;

                case "Legacy Media":
                case "Series":
                case "Video":
                    type = "watching";
                    break;

                case "Demo":
                    type = "demoing";
                    break;

                case "Guide":
                    type = "reading";
                    break;

                case "Hardware":
                    type = "bricking";
                    break;
            }

            command.Reply(
                "{0}{1:N0}{2} {3} {4}{5}{6}{7} -{8} {9}",
                Colors.OLIVE, callback.NumPlayers, Colors.NORMAL,
                type,
                Colors.BLUE, name, Colors.NORMAL,
                callback.Result != EResult.OK ? $" {Colors.RED}({callback.Result}){Colors.NORMAL}" : "",
                Colors.DARKBLUE, SteamDB.GetAppURL(appID, "graphs")
            );
        }
    }
}
