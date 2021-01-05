/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Threading.Tasks;
using Dapper;
using SteamKit2;

namespace SteamDatabaseBackend
{
    internal class PlayersCommand : Command
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
                command.Reply($"Usage:{Colors.OLIVE} players <appid or partial game name>");

                return;
            }

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

            var name = Steam.GetAppName(appID, out var appType);

            if (callback.Result != EResult.OK)
            {
                command.Reply($"Unable to request player count for {Colors.BLUE}{name}{Colors.NORMAL}: {Colors.RED}{callback.Result}{Colors.NORMAL} -{Colors.DARKBLUE} {SteamDB.GetAppUrl(appID, "graphs")}");

                return;
            }

            var numPlayers = callback.NumPlayers;
            uint dailyPlayers;

            await using (var db = await Database.GetConnectionAsync())
            {
                dailyPlayers = await db.ExecuteScalarAsync<uint>("SELECT `MaxDailyPlayers` FROM `OnlineStats` WHERE `AppID` = @appID", new { appID });

                if (appID == 753 && numPlayers == 0)
                {
                    numPlayers = await db.ExecuteScalarAsync<uint>("SELECT `CurrentPlayers` FROM `OnlineStats` WHERE `AppID` = @appID", new { appID });
                }
            }

            if (dailyPlayers < numPlayers)
            {
                dailyPlayers = numPlayers;
            }

            var type = "playing";

            switch (appType)
            {
                case EAppType.Tool:
                case EAppType.Config:
                case EAppType.Application:
                    type = "using";
                    break;

                case EAppType.Media:
                case EAppType.Series:
                case EAppType.Video:
                    type = "watching";
                    break;

                case EAppType.Demo:
                    type = "demoing";
                    break;

                case EAppType.Guide:
                case EAppType.Comic:
                    type = "reading";
                    break;

                case EAppType.Hardware:
                    type = "bricking";
                    break;
            }

            command.Reply(
                $"{Colors.OLIVE}{numPlayers:N0}{Colors.NORMAL} {type} {Colors.BLUE}{name}{Colors.NORMAL} - 24h:{Colors.GREEN} {dailyPlayers:N0}{Colors.NORMAL} -{Colors.DARKBLUE} {SteamDB.GetAppUrl(appID, "graphs")}"
            );
        }
    }
}
