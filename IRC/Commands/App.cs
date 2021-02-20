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
                command.Reply($"Usage:{Colors.OLIVE} app <appid or partial game name>");

                return;
            }

            if (!uint.TryParse(command.Message, out var appID))
            {
                appID = await TrySearchAppId(command);

                if (appID == 0)
                {
                    return;
                }
            }

            var info = await GetAppData(appID);

            if (info == null)
            {
                command.Reply($"Unknown AppID: {Colors.BLUE}{appID}{(LicenseList.OwnedApps.ContainsKey(appID) ? SteamDB.StringCheckmark : string.Empty)}");

                return;
            }

            string name;

            if (info.KeyValues["common"]["name"].Value != null)
            {
                name = Utils.LimitStringLength(Utils.RemoveControlCharacters(info.KeyValues["common"]["name"].AsString()));
            }
            else
            {
                name = Steam.GetAppName(info.ID);
            }
            
            var filename = $"{Utils.ByteArrayToString(info.SHAHash)}.vdf";
            info.KeyValues.SaveToFile(Path.Combine(Application.Path, "app", filename), false);

            command.Reply($"{Colors.BLUE}{name}{Colors.NORMAL} -{Colors.DARKBLUE} <{SteamDB.GetAppUrl(info.ID)}>{Colors.NORMAL} - Dump:{Colors.DARKBLUE} <{SteamDB.GetRawAppUrl(filename)}>{Colors.NORMAL}{(info.MissingToken ? SteamDB.StringNeedToken : string.Empty)}{(LicenseList.OwnedApps.ContainsKey(info.ID) ? SteamDB.StringCheckmark : string.Empty)}");

            if (command.Recipient == Settings.Current.IRC.Channel.Ops && !LicenseList.OwnedApps.ContainsKey(info.ID))
            {
                JobManager.AddJob(() => Steam.Instance.Apps.RequestFreeLicense(info.ID));
            }
        }

        public static async Task<SteamApps.PICSProductInfoCallback.PICSProductInfo> GetAppData(uint appID)
        {
            var tokenTask = Steam.Instance.Apps.PICSGetAccessTokens(appID, null);
            tokenTask.Timeout = TimeSpan.FromSeconds(10);
            var tokenCallback = await tokenTask;
            SteamApps.PICSRequest request;

            if (tokenCallback.AppTokens.ContainsKey(appID))
            {
                request = PICSTokens.NewAppRequest(appID, tokenCallback.AppTokens[appID]);
            }
            else
            {
                request = PICSTokens.NewAppRequest(appID);
            }

            var infoTask = Steam.Instance.Apps.PICSGetProductInfo(new List<SteamApps.PICSRequest> { request }, Enumerable.Empty<SteamApps.PICSRequest>());
            infoTask.Timeout = TimeSpan.FromSeconds(10);
            var job = await infoTask;

            return job.Results?.FirstOrDefault(x => x.Apps.ContainsKey(appID))?.Apps[appID];
        }

        public static async Task<uint> TrySearchAppId(CommandArguments command)
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

                try
                {
                    var response = await Utils.HttpClient.SendAsync(requestMessage);
                    var data = await response.Content.ReadAsStringAsync();
                    var json = JsonConvert.DeserializeObject<AlgoliaSearchAppHits>(data);

                    if (json.Hits.Length > 0)
                    {
                        appID = json.Hits[0].AppID;
                    }
                }
                catch (Exception e)
                {
                    ErrorReporter.Notify("Algolia search", e);
                }
            }

            if (appID > 0)
            {
                return appID;
            }

            await using (var db = await Database.GetConnectionAsync())
            {
                appID = await db.ExecuteScalarAsync<uint>($"SELECT `AppID` FROM `Apps` WHERE (`AppType` IN ({EAppType.Game:d},{EAppType.Application:d},{EAppType.Video:d}) AND (`Apps`.`StoreName` LIKE @Name OR `Apps`.`Name` LIKE @Name)) OR (`AppType` = {EAppType.Invalid:d} AND `Apps`.`LastKnownName` LIKE @Name) ORDER BY `LastUpdated` DESC LIMIT 1", new { Name = name });
            }

            if (appID == 0)
            {
                command.Reply("Nothing was found matching your request.");
            }

            return appID;
        }
    }
}
