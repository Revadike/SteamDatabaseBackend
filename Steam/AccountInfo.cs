/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using SteamKit2;
using SteamKit2.Internal;

namespace SteamDatabaseBackend
{
    internal class AccountInfo : SteamHandler
    {
        public static string Country { get; private set; }

        private static readonly CMsgClientGamesPlayed.GamePlayed InGameShorcut = new CMsgClientGamesPlayed.GamePlayed
        {
            game_extra_info = "\u2764 https://steamdb.info",
            game_id = new GameID
            {
                AppType = GameID.GameType.Shortcut,
                ModID = uint.MaxValue
            }
        };

        public AccountInfo(CallbackManager manager)
        {
            manager.Subscribe<SteamUser.AccountInfoCallback>(OnAccountInfo);

            if (Settings.IsFullRun)
            {
                InGameShorcut.game_extra_info = $"\u23E9 Full run: {Settings.FullRun}";
            }
        }

        private static void OnAccountInfo(SteamUser.AccountInfoCallback callback)
        {
            Country = callback.Country;

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

            Sync();
        }

        public static void Sync()
        {
            var clientMsg = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayed);
            clientMsg.Body.games_played.Add(InGameShorcut);
            Steam.Instance.Client.Send(clientMsg);
        }
    }
}
