/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Text.RegularExpressions;
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

                IRC.Instance.SendReply(e.Recipient,
                    string.Format("{0}\u2937 {1}{2} {3} —{4} {5}",
                        Colors.OLIVE,
                        Colors.NORMAL,
                        isPackage ? "Package" : "App",
                        id,
                        Colors.BLUE,
                        name
                    ),
                    false
                );
            }
        }
    }
}
