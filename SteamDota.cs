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
        public const uint DOTA_2 = 570;

        private static SteamDota _instance = new SteamDota();
        public static SteamDota Instance { get { return _instance; } }

        public SteamClient Client;
        private SteamUser User;
        private SteamFriends Friends;
        public SteamGameCoordinator GameCoordinator;

        public bool IsRunning;

        public System.Timers.Timer timer;

        public void Init()
        {
            IsRunning = true;

            Client = new SteamClient();
            User = Client.GetHandler<SteamUser>();
            Friends = Client.GetHandler<SteamFriends>();
            GameCoordinator = Client.GetHandler<SteamGameCoordinator>();

            CallbackManager CallbackManager = new CallbackManager(Client);

            new Callback<SteamClient.ConnectedCallback>(OnConnected, CallbackManager);
            new Callback<SteamClient.DisconnectedCallback>(OnDisconnected, CallbackManager);
            new Callback<SteamUser.AccountInfoCallback>(OnAccountInfo, CallbackManager);
            new Callback<SteamUser.LoggedOnCallback>(OnLoggedOn, CallbackManager);
            new Callback<SteamGameCoordinator.MessageCallback>(OnGameCoordinatorMessage, CallbackManager);

            timer = new System.Timers.Timer();
            timer.Elapsed += new System.Timers.ElapsedEventHandler(OnTimer);
            timer.Interval = TimeSpan.FromMinutes(5).TotalMilliseconds;

            Client.Connect();

            while (IsRunning)
            {
                CallbackManager.RunWaitCallbacks(TimeSpan.FromSeconds(5));
            }
        }

        private void OnTimer(object sender, System.Timers.ElapsedEventArgs e)
        {

        }

        private void OnGameCoordinatorMessage(SteamGameCoordinator.MessageCallback callback)
        {
            SteamProxy.GameCoordinatorMessage(DOTA_2, callback, GameCoordinator);
        }

        private void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            if (callback.Result == EResult.OK)
            {
                SteamProxy.PlayGame(Client, DOTA_2);

                Thread.Sleep(TimeSpan.FromSeconds(2));

                SteamProxy.GameCoordinatorHello(DOTA_2, GameCoordinator);
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
