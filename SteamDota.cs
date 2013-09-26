/*
 * Copyright (c) 2013, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Threading;
using SteamKit2;

namespace SteamDatabaseBackend
{
    public class SteamDota
    {
        private static SteamDota _instance = new SteamDota();
        public static SteamDota Instance { get { return _instance; } }

        public bool IsRunning;

        public SteamClient Client;
        private SteamUser User;
        private SteamFriends Friends;
        private GameCoordinator GameCoordinator;

        public void Init()
        {
            IsRunning = true;

            Client = new SteamClient();
            User = Client.GetHandler<SteamUser>();
            Friends = Client.GetHandler<SteamFriends>();

            CallbackManager CallbackManager = new CallbackManager(Client);

            new Callback<SteamClient.ConnectedCallback>(OnConnected, CallbackManager);
            new Callback<SteamClient.DisconnectedCallback>(OnDisconnected, CallbackManager);
            new Callback<SteamUser.AccountInfoCallback>(OnAccountInfo, CallbackManager);
            new Callback<SteamUser.LoggedOnCallback>(OnLoggedOn, CallbackManager);

            // game coordinator
            const uint DOTA_2 = 570;

            GameCoordinator = new GameCoordinator(DOTA_2, Client, CallbackManager);

            Client.Connect();

            while (IsRunning)
            {
                CallbackManager.RunWaitCallbacks(TimeSpan.FromSeconds(10));
            }
        }

        private void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            if (callback.Result == EResult.OK)
            {
                GameCoordinator.PlayGame();

                Thread.Sleep(TimeSpan.FromSeconds(2));

                GameCoordinator.Hello();
            }
        }

        private void OnAccountInfo(SteamUser.AccountInfoCallback callback)
        {
            Friends.SetPersonaState(EPersonaState.Busy);
        }

        private void OnConnected(SteamClient.ConnectedCallback callback)
        {
            if (callback.Result != EResult.OK)
            {
                throw new Exception("Could not connect: " + callback.Result);
            }

            Log.WriteInfo("Steam Dota", "Connected! Logging in...");

            User.LogOn(new SteamUser.LogOnDetails
            {
                Username = Settings.Current.SteamDota.Username,
                Password = Settings.Current.SteamDota.Password
            });
        }

        private void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            if (!IsRunning)
            {
                Log.WriteInfo("Steam Dota", "Disconnected from Steam");
                return;
            }

            Log.WriteInfo("Steam Dota", "Disconnected from Steam. Retrying in 15 seconds...");

            Thread.Sleep(TimeSpan.FromSeconds(15));

            Client.Connect();
        }
    }
}
