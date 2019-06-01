/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Dapper;
using Newtonsoft.Json;
using SteamKit2;

namespace SteamDatabaseBackend
{
    internal class AppCommand : Command
    {
#pragma warning disable 0649
        private class AlgoliaSearchAppHits
        {
            public class AlgoliaAppHit
            {
                [JsonProperty(PropertyName = "objectID")]
                public uint AppID;
            }

            public AlgoliaAppHit[] Hits;
        }
#pragma warning restore 0649

        public AppCommand()
        {
            Trigger = "app";
            IsSteamCommand = true;
        }

        public override async Task OnCommand(CommandArguments command)
        {
            if (string.IsNullOrWhiteSpace(command.Message))
            {
                command.Reply("Usage:{0} app <appid or partial game name>", Colors.OLIVE);

                return;
            }

            string name;

            if (!uint.TryParse(command.Message, out var appID))
            {
                appID = await TrySearchAppId(command);

                if (appID == 0)
                {
                    return;
                }
            }

            var tokenTask = Steam.Instance.Apps.PICSGetAccessTokens(appID, null);
            tokenTask.Timeout = TimeSpan.FromSeconds(10);
            var tokenCallback = await tokenTask;
            SteamApps.PICSRequest request;

            if (tokenCallback.AppTokens.ContainsKey(appID))
            {
                request = Utils.NewPICSRequest(appID, tokenCallback.AppTokens[appID]);
            }
            else
            {
                request = Utils.NewPICSRequest(appID);
            }

            var infoTask = Steam.Instance.Apps.PICSGetProductInfo(new List<SteamApps.PICSRequest> { request }, Enumerable.Empty<SteamApps.PICSRequest>());
            infoTask.Timeout = TimeSpan.FromSeconds(10);
            var job = await infoTask;
            var callback = job.Results.FirstOrDefault(x => x.Apps.ContainsKey(appID));

            if (callback == null)
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

            if (command.IsUserAdmin && !LicenseList.OwnedApps.ContainsKey(info.ID))
            {
                JobManager.AddJob(() => Steam.Instance.Apps.RequestFreeLicense(info.ID));
            }
        }

        public async static Task<uint> TrySearchAppId(CommandArguments command)
        {
            uint appID = 0;
            var name = command.Message;
            Uri uri;
            var query = new Dictionary<string, string>
            {
                { "hitsPerPage", "1" },
                { "attributesToHighlight", "null" },
                { "attributesToSnippet", "null" },
                { "attributesToRetrieve", "[\"objectID\"]" },
                { "facetFilters", "[[\"appType:Game\",\"appType:Application\"]]" },
                { "advancedSyntax", "true" },
                { "query", name }
            };

            using (var content = new FormUrlEncodedContent(query))
            {
                uri = new UriBuilder("https://94he6yatei-dsn.algolia.net/1/indexes/steamdb/")
                {
                    Query = await content.ReadAsStringAsync()
                }.Uri;
            }

            using (var requestMessage = new HttpRequestMessage(HttpMethod.Get, uri))
            {
                requestMessage.Headers.Add("Referer", "https://github.com/SteamDatabase/SteamDatabaseBackend");
                requestMessage.Headers.Add("X-Algolia-Application-Id", "94HE6YATEI");
                requestMessage.Headers.Add("X-Algolia-API-Key", "2414d3366df67739fe6e73dad3f51a43");

                var response = await Utils.HttpClient.SendAsync(requestMessage);
                var data = await response.Content.ReadAsStringAsync();
                var json = JsonConvert.DeserializeObject<AlgoliaSearchAppHits>(data);

                if (json.Hits.Length > 0)
                {
                    appID = json.Hits[0].AppID;
                }
            }

            if (appID > 0)
            {
                return appID;
            }

            using (var db = Database.Get())
            {
                appID = await db.ExecuteScalarAsync<uint>("SELECT `AppID` FROM `Apps` LEFT JOIN `AppsTypes` ON `Apps`.`AppType` = `AppsTypes`.`AppType` WHERE (`AppsTypes`.`Name` IN ('game', 'application', 'video', 'hardware') AND (`Apps`.`StoreName` LIKE @Name OR `Apps`.`Name` LIKE @Name)) OR (`AppsTypes`.`Name` = 'unknown' AND `Apps`.`LastKnownName` LIKE @Name) ORDER BY `LastUpdated` DESC LIMIT 1", new { Name = name });
            }

            if (appID == 0)
            {
                command.Reply("Nothing was found matching your request.");
            }

            return appID;
        }
    }
}
