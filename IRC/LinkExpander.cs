/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Dapper;
using NetIrc2.Events;

namespace SteamDatabaseBackend
{
    public class LinkExpander
    {
        private readonly Regex SteamLinkMatch;

        public LinkExpander()
        {
            SteamLinkMatch = new Regex(@"(?:^|/|\.)steam(?:community|powered)\.com/(?<type>sub|app|games|stats)/(?<id>[0-9]{1,7})", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);
        }

        public void OnMessage(ChatMessageEventArgs e)
        {
            var matches = SteamLinkMatch.Matches(e.Message);

            foreach (Match match in matches)
            {
                var id = uint.Parse(match.Groups["id"].Value);
                var isPackage = match.Groups["type"].Value == "sub";
                var name = isPackage ? Steam.GetPackageName(id) : Steam.GetAppName(id);

                if (e.Message.ToString().Contains(name))
                {
                    continue;
                }

                string priceInfo = string.Empty;

                if (!isPackage)
                {
                    List<Price> prices;

                    using (var db = Database.GetConnection())
                    {
                        prices = db.Query<Price>("SELECT `Country`, `PriceFinal`, `PriceDiscount` FROM `Store` WHERE `AppID` = @AppID AND `Country` IN ('us', 'uk', 'it')", new { AppID = id }).ToList();
                    }

                    priceInfo = string.Format(" {0}({1})", Colors.LIGHTGRAY, string.Join(" / ", prices.Select(x => x.Format())));
                }

                IRC.Instance.SendReply(e.Recipient,
                    string.Format("{0}\u2937 {1}{2} {3} —{4} {5}{6}{7}",
                        Colors.OLIVE,
                        Colors.NORMAL,
                        isPackage ? "Package" : "App",
                        id,
                        Colors.BLUE,
                        name,
                        Colors.NORMAL,
                        priceInfo
                    ),
                    false
                );
            }
        }
    }
}
