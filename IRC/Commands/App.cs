/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dapper;
using SteamKit2;

namespace SteamDatabaseBackend
{
    class AppCommand : Command
    {
        public AppCommand()
        {
            Trigger = "app";
            IsSteamCommand = true;
        }

        public override async void OnCommand(CommandArguments command)
        {
            if (command.Message.Length == 0)
            {
                command.Reply("Usage:{0} app <appid or partial game name>", Colors.OLIVE);

                return;
            }

            var count = PICSProductInfo.ProcessedApps.Count;

            if (count > 100)
            {
                command.Reply("There are currently {0} apps awaiting to be processed, try again later.", count);

                return;
            }

            uint appID;
            string name;

            if (!uint.TryParse(command.Message, out appID))
            {
                name = command.Message;

                if (!Utils.ConvertUserInputToSQLSearch(ref name))
                {
                    command.Reply("Your request is invalid or too short.");

                    return;
                }

                using (var db = Database.GetConnection())
                {
                    appID = db.ExecuteScalar<uint>("SELECT `AppID` FROM `Apps` WHERE `Apps`.`StoreName` LIKE @Name OR `Apps`.`Name` LIKE @Name OR `Apps`.`LastKnownName` LIKE @Name ORDER BY `LastUpdated` DESC LIMIT 1", new { Name = name });
                }

                if (appID == 0)
                {
                    command.Reply("Nothing was found matching your request.");

                    return;
                }
            }

            var tokenCallback = await Steam.Instance.Apps.PICSGetAccessTokens(new List<uint> { appID }, Enumerable.Empty<uint>());
            SteamApps.PICSRequest request;

            if (tokenCallback.AppTokens.ContainsKey(appID))
            {
                request = Utils.NewPICSRequest(appID, tokenCallback.AppTokens[appID]);
            }
            else
            {
                request = Utils.NewPICSRequest(appID);
            }

            var job = await Steam.Instance.Apps.PICSGetProductInfo(new List<SteamApps.PICSRequest> { request }, Enumerable.Empty<SteamApps.PICSRequest>());
            var callback = job.Results.First(x => !x.ResponsePending);

            if (!callback.Apps.ContainsKey(appID))
            {
                command.Reply("Unknown AppID: {0}{1}{2}", Colors.BLUE, appID, LicenseList.OwnedApps.ContainsKey(appID) ? SteamDB.StringCheckmark : string.Empty);

                return;
            }

            var info = callback.Apps[appID];

            if (info.KeyValues["common"]["name"].Value != null)
            {
                name = Utils.RemoveControlCharacters(info.KeyValues["common"]["name"].AsString());
            }
            else
            {
                name = Steam.GetAppName(info.ID);
            }

            info.KeyValues.SaveToFile(Path.Combine(Application.Path, "app", string.Format("{0}.vdf", info.ID)), false);

            command.Reply("{0}{1}{2} -{3} {4}{5} - Dump:{6} {7}{8}{9}{10}",
                Colors.BLUE, name, Colors.NORMAL,
                Colors.DARKBLUE, SteamDB.GetAppURL(info.ID), Colors.NORMAL,
                Colors.DARKBLUE, SteamDB.GetRawAppURL(info.ID), Colors.NORMAL,
                info.MissingToken ? SteamDB.StringNeedToken : string.Empty,
                LicenseList.OwnedApps.ContainsKey(info.ID) ? SteamDB.StringCheckmark : string.Empty
            );
        }
    }
}
