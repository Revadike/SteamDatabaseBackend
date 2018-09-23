/*
 * Copyright (c) 2013-2018, SteamDB. All rights reserved.
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
            SteamLinkMatch = new Regex(
                @"(?:^|/|\.)steam(?:community|powered)\.com/(?<type>sub|app|games|stats)/(?<id>[0-9]{1,7})(?:/(?<page>[a-z]+)/.)?",
                RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture
            );
        }

        public void OnMessage(CommandArguments command)
        {
            var matches = SteamLinkMatch.Matches(command.Message);

            foreach (Match match in matches)
            {
                var page = match.Groups["page"].Value;

                // Ignore sub pages, easier to do it here rather than in regex
                if (!string.IsNullOrEmpty(page))
                {
                    continue;
                }

                var appType = string.Empty;
                var id = uint.Parse(match.Groups["id"].Value);
                var isPackage = match.Groups["type"].Value == "sub";
                string name;

                if (isPackage)
                {
                    name = Steam.GetPackageName(id);
                }
                else
                {
                    App data;

                    using (var db = Database.Get())
                    {
                        data = db.Query<App>("SELECT `AppID`, `Apps`.`Name`, `LastKnownName`, `AppsTypes`.`DisplayName` as `AppTypeString` FROM `Apps` JOIN `AppsTypes` ON `Apps`.`AppType` = `AppsTypes`.`AppType` WHERE `AppID` = @AppID", new { AppID = id }).SingleOrDefault();
                    }

                    if (data.AppID == 0)
                    {
                        continue;
                    }

                    name = string.IsNullOrEmpty(data.LastKnownName) ? data.Name : data.LastKnownName;
                    name = Utils.RemoveControlCharacters(name);
                    appType = data.AppTypeString;
                }

                if (command.Message.IndexOf(name, StringComparison.CurrentCultureIgnoreCase) >= 0)
                {
                    continue;
                }

                string priceInfo = isPackage ? string.Empty : GetFormattedPrices(id);

                if (command.CommandType == ECommandType.SteamChatRoom)
                {
                    Steam.Instance.Friends.SendChatRoomMessage(command.ChatRoomID, EChatEntryType.ChatMsg, string.Format("» {0} {1} — {2}{3}", isPackage ? "Package" : "App", id, Colors.StripColors(name), priceInfo));

                    continue;
                }

                IRC.Instance.SendReply(command.Recipient,
                    string.Format("{0}» {1}{2} {3} —{4} {5}{6}{7}",
                        Colors.OLIVE,
                        Colors.NORMAL,
                        isPackage ? "Package" : appType,
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

        public static string GetFormattedPrices(uint appID)
        {
            string priceInfo = string.Empty;

            if (!Settings.Current.CanQueryStore)
            {
                return priceInfo;
            }

            List<Price> prices;

            using (var db = Database.Get())
            {
                prices = db.Query<Price>("SELECT `Country`, `PriceFinal`, `PriceDiscount` FROM `Store` WHERE `AppID` = @AppID AND `Country` IN ('us', 'uk', 'eu')", new { AppID = appID }).ToList();
            }

            if (prices.Any())
            {
                priceInfo = string.Format(" ({0})", string.Join(" / ", prices.Select(x => x.Format())));
            }

            return priceInfo;
        }
    }
}
