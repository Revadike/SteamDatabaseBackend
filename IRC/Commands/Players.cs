/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Threading.Tasks;
using SteamKit2;
using Dapper;

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
            if (string.IsNullOrWhiteSpace(command.Message))
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
                    return;
                }
            }

            var task = Steam.Instance.UserStats.GetNumberOfCurrentPlayers(appID);
            task.Timeout = TimeSpan.FromSeconds(10);
            var callback = await task;

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

            uint dailyPlayers;

            using (var db = Database.Get())
            {
                dailyPlayers = await db.ExecuteScalarAsync<uint>("SELECT `MaxDailyPlayers` FROM `OnlineStats` WHERE `AppID` = @appID", new { appID });
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
                $"{Colors.OLIVE}{callback.NumPlayers:N0}{Colors.NORMAL} {type} {Colors.BLUE}{name}{Colors.NORMAL} (24h:{Colors.GREEN} {dailyPlayers:N0}{Colors.NORMAL}) -{Colors.DARKBLUE} {SteamDB.GetAppURL(appID, "graphs")}"
            );
        }
    }
}
