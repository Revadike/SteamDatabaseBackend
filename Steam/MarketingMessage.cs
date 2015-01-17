/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
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
            manager.Register(new Callback<SteamUser.MarketingMessageCallback>(OnMarketingMessage));
        }

        private static void OnMarketingMessage(SteamUser.MarketingMessageCallback callback)
        {
            List<GlobalID> ids;

            using (var db = Database.GetConnection())
            {
                ids = db.Query<GlobalID>("SELECT `ID` FROM `MarketingMessages` WHERE `ID` IN @Ids", new { Ids = callback.Messages.Select(x => x.ID) }).ToList();
            }

            foreach (var message in callback.Messages)
            {
                if (ids.Contains(message.ID))
                {
                    continue;
                }

                if (message.Flags == EMarketingMessageFlags.None)
                {
                    IRC.Instance.SendMain("New marketing message:{0} {1}", Colors.DARKBLUE, message.URL);
                }
                else
                {
                    IRC.Instance.SendMain("New marketing message:{0} {1} {2}({3})", Colors.DARKBLUE, message.URL, Colors.DARKGRAY, message.Flags.ToString().Replace("Platform", string.Empty));
                }

                using (var db = Database.GetConnection())
                {
                    db.Execute("INSERT INTO `MarketingMessages` (`ID`, `Flags`) VALUES (@ID, @Flags)", message);
                }
            }
        }
    }
}
