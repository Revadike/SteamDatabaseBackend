/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using SteamKit2;

namespace SteamDatabaseBackend
{
    class ChatMemberInfo : SteamHandler
    {
        public ChatMemberInfo(CallbackManager manager)
            : base(manager)
        {
            manager.Subscribe<SteamFriends.ChatMemberInfoCallback>(OnChatMemberInfo);
        }

        private static void OnChatMemberInfo(SteamFriends.ChatMemberInfoCallback callback)
        {
            if (callback.Type != EChatInfoType.StateChange || callback.StateChangeInfo.ChatterActedOn != Steam.Instance.Client.SteamID)
            {
                return;
            }

            Log.WriteInfo("ChatMemberInfo", "State changed for chatroom {0} to {1}", callback.ChatRoomID, callback.StateChangeInfo.StateChange);

            if (callback.StateChangeInfo.StateChange == EChatMemberStateChange.Disconnected
            ||  callback.StateChangeInfo.StateChange == EChatMemberStateChange.Kicked
            ||  callback.StateChangeInfo.StateChange == EChatMemberStateChange.Left)
            {
                Steam.Instance.Friends.JoinChat(callback.ChatRoomID);

                Steam.Instance.Friends.SendChatRoomMessage(
                    callback.ChatRoomID,
                    EChatEntryType.ChatMsg,
                    string.Format("{0}, please don't do that again :(", Steam.Instance.Friends.GetFriendPersonaName(callback.StateChangeInfo.ChatterActedBy))
                );
            }
        }
    }
}
