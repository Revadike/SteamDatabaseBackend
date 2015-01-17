/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System.Collections.Generic;
using System.Linq;
using Dapper;

namespace SteamDatabaseBackend
{
    class AppCommand : Command
    {
        public AppCommand()
        {
            Trigger = "!app";
            IsSteamCommand = true;
        }

        public override void OnCommand(CommandArguments command)
        {
            if (command.Message.Length == 0)
            {
                CommandHandler.ReplyToCommand(command, "Usage:{0} !app <appid or partial game name>", Colors.OLIVE);

                return;
            }

            uint appID;

            if (!uint.TryParse(command.Message, out appID))
            {
                string name = command.Message;

                if (!Utils.ConvertUserInputToSQLSearch(ref name))
                {
                    CommandHandler.ReplyToCommand(command, "Your request is invalid or too short.");

                    return;
                }

                using (var db = Database.GetConnection())
                {
                    var app = db.ExecuteScalar<App?>("SELECT `AppID` FROM `Apps` WHERE `Apps`.`StoreName` LIKE @Name OR `Apps`.`Name` LIKE @Name ORDER BY `LastUpdated` DESC LIMIT 1", new { Name = name });

                    if (app.HasValue)
                    {
                        appID = app.Value.AppID;
                    }
                    else
                    {
                        CommandHandler.ReplyToCommand(command, "Nothing was found matching your request.");

                        return;
                    }
                }
            }

            var apps = new List<uint>();

            apps.Add(appID);

            JobManager.AddJob(
                () => Steam.Instance.Apps.PICSGetAccessTokens(apps, Enumerable.Empty<uint>()), 
                new JobManager.IRCRequest
                {
                    Target = appID,
                    Type = JobManager.IRCRequestType.TYPE_APP,
                    Command = command
                }
            );
        }
    }
}
