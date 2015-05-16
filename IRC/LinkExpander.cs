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
using SteamKit2;

namespace SteamDatabaseBackend
{
    class LinkExpander
    {
        private readonly Regex SteamLinkMatch;

        public LinkExpander()
        {
            SteamLinkMatch = new Regex(@"(?:^|/|\.)steam(?:community|powered)\.com/(?<type>sub|app|games|stats)/(?<id>[0-9]{1,7})", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);
        }

        public void OnMessage(CommandArguments command)
        {
            var matches = SteamLinkMatch.Matches(command.Message);

            foreach (Match match in matches)
            {
                var id = uint.Parse(match.Groups["id"].Value);
                var isPackage = match.Groups["type"].Value == "sub";
                var name = isPackage ? Steam.GetPackageName(id) : Steam.GetAppName(id);

                if (command.Message.Contains(name))
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

                    priceInfo = string.Format(" ({0})", string.Join(" / ", prices.Select(x => x.Format())));
                }

                if (command.CommandType == ECommandType.SteamChatRoom)
                {
                    Steam.Instance.Friends.SendChatRoomMessage(command.ChatRoomID, EChatEntryType.ChatMsg, string.Format("\u2937 {0} {1} — {2}{3}", isPackage ? "Package" : "App", id, Colors.StripColors(name), priceInfo));
                }
                else
                {
                    IRC.Instance.SendReply(command.Recipient,
                        string.Format("{0}\u2937 {1}{2} {3} —{4} {5}{6}{7}",
                            Colors.OLIVE,
                            Colors.NORMAL,
                            isPackage ? "Package" : "App",
                            id,
                            Colors.BLUE,
                            name,
                            Colors.LIGHTGRAY,
                            priceInfo
                        ),
                        false
                    );
                }
            }
        }
    }
}
