/*
 * Copyright (c) 2013-2018, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using SteamKit2;

namespace SteamDatabaseBackend
{
    class ClanState : SteamHandler
    {
        public ClanState(CallbackManager manager)
            : base(manager)
        {
            manager.Subscribe<SteamFriends.ClanStateCallback>(OnClanState);
        }

        private static void OnClanState(SteamFriends.ClanStateCallback callback)
        {
            if (callback.Events.Count == 0 && callback.Announcements.Count == 0)
            {
                return;
            }

            string groupName = callback.ClanName;
            string message;

            if (string.IsNullOrEmpty(groupName))
            {
                groupName = Steam.Instance.Friends.GetClanName(callback.ClanID);

                // Check once more, because that can fail too
                if (string.IsNullOrEmpty(groupName))
                {
                    groupName = "Group";
                }
            }

            foreach (var announcement in callback.Announcements)
            {
                message = string.Format(
                    "{0}{1}{2} announcement: {3}{4}{5} -{6} https://steamcommunity.com/gid/{7}/announcements/detail/{8}",
                    Colors.BLUE, groupName, Colors.NORMAL,
                    Colors.OLIVE, announcement.Headline, Colors.NORMAL,
                    Colors.DARKBLUE, callback.ClanID.ConvertToUInt64(), announcement.ID
                );

                IRC.Instance.SendMain(message);

                Log.WriteInfo("Group Announcement", "{0} \"{1}\"", groupName, announcement.Headline);
            }

            foreach (var groupEvent in callback.Events)
            {
                if (!groupEvent.JustPosted)
                {
                    continue;
                }

                message = string.Format(
                    "{0}{1}{2} event: {3}{4}{5} -{6} https://steamcommunity.com/gid/{7}/events/{8} {9}({10})",
                    Colors.BLUE, groupName, Colors.NORMAL,
                    Colors.OLIVE, groupEvent.Headline, Colors.NORMAL,
                    Colors.DARKBLUE, callback.ClanID, groupEvent.ID,
                    Colors.DARKGRAY, groupEvent.EventTime
                );

                IRC.Instance.SendMain(message);

                Log.WriteInfo("Group Announcement", "{0} Event \"{1}\"", groupName, groupEvent.Headline);
            }
        }
    }
}
