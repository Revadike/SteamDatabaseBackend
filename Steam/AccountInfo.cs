/*
 * Copyright (c) 2013-2018, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Generic;
using System.Linq;
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
                Settings.Current.GameCoordinatorIdlers
                    .Concat(AppsToIdle)
                    .Select(appID => new CMsgClientGamesPlayed.GamePlayed
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

                var data = await client.GetStringAsync("https://localhost/api/GetNextAppIdToIdle/");
                newAppsToIdle = JsonConvert.DeserializeObject<List<uint>>(data);
            }
            
            if (!AppsToIdle.SequenceEqual(newAppsToIdle))
            {
                Log.WriteInfo("AccountInfo", $"{newAppsToIdle.Count} apps to idle: {string.Join(", ", newAppsToIdle)}");

                AppsToIdle = newAppsToIdle;
                Sync();
            }
            else
            {
                Log.WriteInfo("AccountInfo", $"Idling the same {AppsToIdle.Count} apps");
            }
        }
    }
}
