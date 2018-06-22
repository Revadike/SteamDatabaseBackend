/*
 * Copyright (c) 2013-2018, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System.Linq;
using SteamKit2;
using SteamKit2.Internal;

namespace SteamDatabaseBackend
{
    class AccountInfo : SteamHandler
    {
        public static string Country { get; private set; }

        public AccountInfo(CallbackManager manager)
            : base(manager)
        {
            manager.Subscribe<SteamUser.AccountInfoCallback>(OnAccountInfo);
        }

        private static void OnAccountInfo(SteamUser.AccountInfoCallback callback)
        {
            Country = callback.Country;

            if (!Settings.IsFullRun)
            {
                Sync();
            }
        }

        public static void Sync()
        {
            var clientMsg = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayedNoDataBlob);
            clientMsg.Body.games_played.AddRange(
                Settings.Current.GameCoordinatorIdlers.Select(appID => new CMsgClientGamesPlayed.GamePlayed
                {
                    game_extra_info = "\u2764 steamdb.info",
                    game_id = appID
                })
            );

            Steam.Instance.Client.Send(clientMsg);

            //Steam.Instance.Friends.SetPersonaState(EPersonaState.Busy);
            var stateMsg = new ClientMsgProtobuf<CMsgClientChangeStatus>(EMsg.ClientChangeStatus)
            {
                Body =
                {
                    persona_state = (uint)EPersonaState.Online,
                    persona_state_flags = uint.MaxValue,
                    player_name = Steam.Instance.Friends.GetPersonaName()
                }
            };

            Steam.Instance.Client.Send(stateMsg);

            foreach(var chatRoom in Settings.Current.ChatRooms)
            {
                Steam.Instance.Friends.JoinChat(chatRoom);
            }
        }
    }
}
