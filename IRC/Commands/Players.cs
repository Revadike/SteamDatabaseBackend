/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using MySql.Data.MySqlClient;
using SteamKit2;

namespace SteamDatabaseBackend
{
    class PlayersCommand : Command
    {
        public PlayersCommand()
        {
            Trigger = "!players";

            Steam.Instance.CallbackManager.Register(new Callback<SteamUserStats.NumberOfPlayersCallback>(OnNumberOfPlayers));
        }

        public override void OnCommand(CommandArguments command)
        {
            if (command.Message.Length == 0)
            {
                CommandHandler.ReplyToCommand(command, "Usage:{0} !players <appid or partial game name>", Colors.OLIVE);

                return;
            }

            uint appID;

            if (!uint.TryParse(command.Message, out appID))
            {
                string name = command.Message;

                if (!Utils.ConvertUserInputToSQLSearch(ref name))
                {
                    CommandHandler.ReplyToCommand(command, "Your request is too short.");

                    return;
                }

                using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `AppID` FROM `Apps` LEFT JOIN `AppsTypes` ON `Apps`.`AppType` = `AppsTypes`.`AppType` WHERE `AppsTypes`.`Name` IN ('game', 'application') AND (`Apps`.`StoreName` LIKE @Name OR `Apps`.`Name` LIKE @Name) ORDER BY `LastUpdated` DESC LIMIT 1", new MySqlParameter("Name", name)))
                {
                    if (Reader.Read())
                    {
                        appID = Reader.GetUInt32("AppID");
                    }
                    else
                    {
                        CommandHandler.ReplyToCommand(command, "Nothing was found matching your request.");

                        return;
                    }
                }
            }

            JobManager.AddJob(
                () => Steam.Instance.UserStats.GetNumberOfCurrentPlayers(appID),
                new JobManager.IRCRequest
                {
                    Target = appID,
                    Command = command
                }
            );
        }

        private static void OnNumberOfPlayers(SteamUserStats.NumberOfPlayersCallback callback)
        {
            JobAction job;

            if (!JobManager.TryRemoveJob(callback.JobID, out job) || !job.IsCommand)
            {
                return;
            }

            var request = job.CommandRequest;

            if (callback.Result != EResult.OK)
            {
                CommandHandler.ReplyToCommand(request.Command, "Unable to request player count: {0}", callback.Result);
            }
            else if (request.Target == 0)
            {
                CommandHandler.ReplyToCommand(
                    request.Command,
                    "{0}{1:N0}{2} people praising lord Gaben right now, influence:{3} {4}",
                    Colors.OLIVE, callback.NumPlayers, Colors.NORMAL,
                    Colors.DARKBLUE, SteamDB.GetAppURL(753, "graph")
                );
            }
            else
            {
                CommandHandler.ReplyToCommand(
                    request.Command,
                    "People playing {0}{1}{2} right now: {3}{4:N0}{5} -{6} {7}",
                    Colors.OLIVE, Steam.GetAppName(request.Target), Colors.NORMAL,
                    Colors.GREEN, callback.NumPlayers, Colors.NORMAL,
                    Colors.DARKBLUE, SteamDB.GetAppURL(request.Target)
                );
            }
        }
    }
}
