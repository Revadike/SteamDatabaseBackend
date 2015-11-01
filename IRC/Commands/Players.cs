/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System.Threading.Tasks;
using Dapper;
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
                command.Notice("Use {0}^{1} and {2}${3} just like in regex to narrow down your match, e.g:{4} !players Portal$", Colors.BLUE, Colors.NORMAL, Colors.BLUE, Colors.NORMAL, Colors.OLIVE);

                return;
            }

            uint appID;

            if (!uint.TryParse(command.Message, out appID))
            {
                string name = command.Message;

                if (!Utils.ConvertUserInputToSQLSearch(ref name))
                {
                    command.Reply("Your request is invalid or too short.");

                    return;
                }

                using (var db = Database.GetConnection())
                {
                    appID = db.ExecuteScalar<uint>("SELECT `AppID` FROM `Apps` LEFT JOIN `AppsTypes` ON `Apps`.`AppType` = `AppsTypes`.`AppType` WHERE (`AppsTypes`.`Name` IN ('game', 'application') AND (`Apps`.`StoreName` LIKE @Name OR `Apps`.`Name` LIKE @Name)) OR (`AppsTypes`.`Name` = 'unknown' AND `Apps`.`LastKnownName` LIKE @Name) ORDER BY `LastUpdated` DESC LIMIT 1", new { Name = name });
                }

                if (appID == 0)
                {
                    command.Reply("Nothing was found matching your request.");

                    return;
                }
            }

            var callback = await Steam.Instance.UserStats.GetNumberOfCurrentPlayers(appID);

            if (callback.Result != EResult.OK)
            {
                command.Reply("Unable to request player count: {0}{1}", Colors.RED, callback.Result);
            }
            else if (appID == 0)
            {
                command.Reply(
                    "{0}{1:N0}{2} people praising lord Gaben right now, influence:{3} {4}",
                    Colors.OLIVE, callback.NumPlayers, Colors.NORMAL,
                    Colors.DARKBLUE, SteamDB.GetAppURL(753, "graphs")
                );
            }
            else
            {
                command.Reply(
                    "People playing {0}{1}{2} right now: {3}{4:N0}{5} -{6} {7}",
                    Colors.BLUE, Steam.GetAppName(appID), Colors.NORMAL,
                    Colors.OLIVE, callback.NumPlayers, Colors.NORMAL,
                    Colors.DARKBLUE, SteamDB.GetAppURL(appID, "graphs")
                );
            }
        }
    }
}
