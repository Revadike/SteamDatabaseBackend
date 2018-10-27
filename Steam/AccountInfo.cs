/*
 * Copyright (c) 2013-2018, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SteamKit2;
using SteamKit2.Internal;

namespace SteamDatabaseBackend
{
    class AccountInfo : SteamHandler
    {
        public static string Country { get; private set; }

        private static List<uint> AppsToIdle = new List<uint>();

        private static CMsgClientGamesPlayed.GamePlayed InGameShorcut = new CMsgClientGamesPlayed.GamePlayed
        {
            game_extra_info = "\u2764 https://steamdb.info",
            game_id = new GameID
            {
                AppType = GameID.GameType.Shortcut,
                ModID = uint.MaxValue
            }
        };

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

                foreach (var chatRoom in Settings.Current.ChatRooms)
                {
                    Steam.Instance.Friends.JoinChat(chatRoom);
                }
            }
        }

        public static void Sync()
        {
            var clientMsg = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayed);

            // Send empty first
            Steam.Instance.Client.Send(clientMsg);

            if (AppsToIdle.Count > 0)
            {
                clientMsg.Body.games_played.AddRange(
                    AppsToIdle.Select(appID => new CMsgClientGamesPlayed.GamePlayed
                    {
                        game_extra_info = InGameShorcut.game_extra_info,
                        game_id = appID
                    })
                );
            }
            else
            {
                clientMsg.Body.games_played.Add(InGameShorcut);
            }

            Steam.Instance.Client.Send(clientMsg);
        }

        public async static Task RefreshAppsToIdle()
        {
            if (!Settings.Current.CanQueryStore)
            {
                return;
            }

            List<uint> newAppsToIdle;

            using (var handler = new HttpClientHandler())
            using (var client = new HttpClient(handler))
            {
                handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
                client.DefaultRequestHeaders.Add("Host", "steamdb.info");

                var response = await client.GetAsync("https://localhost/api/GetNextAppIdToIdle/");

                if (!response.IsSuccessStatusCode)
                {
                    Log.WriteWarn("AccountInfo", $"GetNextAppIdToIdle returned {response.StatusCode}");

                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        await WebAuth.AuthenticateUser();
                    }

                    return;
                }

                var data = await response.Content.ReadAsStringAsync();
                newAppsToIdle = JsonConvert.DeserializeObject<List<uint>>(data);
            }

            Log.WriteInfo("AccountInfo", $"{newAppsToIdle.Count} apps to idle: {string.Join(", ", newAppsToIdle)}");

            if (!AppsToIdle.SequenceEqual(newAppsToIdle))
            {
                AppsToIdle = newAppsToIdle;
                Sync();
            }
        }
    }
}
