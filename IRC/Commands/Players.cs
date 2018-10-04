/*
 * Copyright (c) 2013-2018, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Net;
using System.Threading.Tasks;
using Dapper;
using Newtonsoft.Json;
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
                name = command.Message;

                using (var webClient = new WebClient())
                {
                    webClient.Headers.Add("Referer", "https://github.com/SteamDatabase/SteamDatabaseBackend");
                    webClient.Headers.Add("X-Algolia-Application-Id", "94HE6YATEI");
                    webClient.Headers.Add("X-Algolia-API-Key", "2414d3366df67739fe6e73dad3f51a43");
                    webClient.QueryString.Add("hitsPerPage", "1");
                    webClient.QueryString.Add("attributesToHighlight", "null");
                    webClient.QueryString.Add("attributesToSnippet", "null");
                    webClient.QueryString.Add("attributesToRetrieve", "[\"objectID\"]");
                    webClient.QueryString.Add("facetFilters", "[[\"appType:Game\",\"appType:Application\"]]");
                    webClient.QueryString.Add("advancedSyntax", "true");
                    webClient.QueryString.Add("query", name);

                    var data = await webClient.DownloadStringTaskAsync("https://94he6yatei-dsn.algolia.net/1/indexes/steamdb/");
                    dynamic json = JsonConvert.DeserializeObject(data);

                    if (json.hits != null && json.hits.Count > 0)
                    {
                        appID = json.hits[0].objectID;
                    }
                }

                if (appID == 0)
                {
                    if (!Utils.ConvertUserInputToSQLSearch(ref name))
                    {
                        command.Reply ("Your request is invalid or too short.");

                        return;
                    }

                    using (var db = Database.Get())
                    {
                        appID = await db.ExecuteScalarAsync<uint>("SELECT `AppID` FROM `Apps` LEFT JOIN `AppsTypes` ON `Apps`.`AppType` = `AppsTypes`.`AppType` WHERE (`AppsTypes`.`Name` IN ('game', 'application', 'video', 'hardware') AND (`Apps`.`StoreName` LIKE @Name OR `Apps`.`Name` LIKE @Name)) OR (`AppsTypes`.`Name` = 'unknown' AND `Apps`.`LastKnownName` LIKE @Name) ORDER BY `LastUpdated` DESC LIMIT 1", new { Name = name });
                    }
                }

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
