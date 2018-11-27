/*
 * Copyright (c) 2013-2018, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Linq;
using Dapper;
using SteamKit2;
using System.Collections.Generic;

namespace SteamDatabaseBackend
{
    class MarketingMessage : SteamHandler
    {
        public MarketingMessage(CallbackManager manager)
            : base(manager)
        {
            manager.Subscribe<SteamUser.MarketingMessageCallback>(OnMarketingMessage);
        }

        private static async void OnMarketingMessage(SteamUser.MarketingMessageCallback callback)
        {
            if (callback.Messages.Count == 0)
            {
                return;
            }

            using (var db = await Database.GetConnectionAsync())
            {
                var items = (await db.QueryAsync<RSS.GenericFeedItem>("SELECT `Link` FROM `RSS` WHERE `Link` IN @Ids", new {Ids = callback.Messages.Select(x => x.URL)})).ToDictionary(x => x.Link, _ => (byte)1);
                var newMessages = callback.Messages.Where(item => !items.ContainsKey(item.URL));

                foreach (var message in newMessages)
                {
                    Log.WriteInfo("Marketing", $"{message.ID} {message.URL} ({message.Flags})");

                    if (message.Flags == EMarketingMessageFlags.None)
                    {
                        IRC.Instance.SendMain($"New marketing message:{Colors.DARKBLUE} {message.URL}");
                    }
                    else
                    {
                        IRC.Instance.SendMain($"New marketing message:{Colors.DARKBLUE} {message.URL} {Colors.DARKGRAY}({message.Flags.ToString().Replace("Platform", string.Empty)})");
                    }

                    await db.ExecuteAsync("INSERT INTO `RSS` (`Link`, `Title`) VALUES(@URL, @Title)", new { message.URL, Title = $"Marketing #{message.ID}" });
                }
            }
        }
    }
}
