/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using SteamKit2;
using SteamKit2.Internal;
using System.Linq;

namespace SteamDatabaseBackend
{
    class AccountInfo : SteamHandler
    {
        public AccountInfo(CallbackManager manager)
            : base(manager)
        {
            manager.Register(new Callback<SteamUser.AccountInfoCallback>(OnAccountInfo));
        }

        private static void OnAccountInfo(SteamUser.AccountInfoCallback callback)
        {
            Sync();
        }

        public static void Sync()
        {
            Steam.Instance.Friends.SetPersonaState(EPersonaState.Busy);

            foreach (var chatRoom in Settings.Current.ChatRooms)
            {
                Steam.Instance.Friends.JoinChat(chatRoom);
            }

            var clientMsg = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayedNoDataBlob);

            clientMsg.Body.games_played.AddRange(
                Settings.Current.GameCoordinatorIdlers.Select(appID => new CMsgClientGamesPlayed.GamePlayed
                {
                    game_id = appID
                })
            );

            Steam.Instance.Client.Send( clientMsg );
        }
    }
}
