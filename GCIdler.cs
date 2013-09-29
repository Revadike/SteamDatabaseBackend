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
    public class GCIdler
    {
        public bool IsRunning;

        public SteamClient Client;
        private SteamUser User;
        private SteamFriends Friends;
        private CallbackManager CallbackManager;
        private GameCoordinator GameCoordinator;

        private uint AppID;
        private string Username;
        private string Password;

        public GCIdler(uint appID, string username, string password)
        {
            Username = username;
            Password = password;
            AppID = appID;
            
            Client = new SteamClient();
            User = Client.GetHandler<SteamUser>();
            Friends = Client.GetHandler<SteamFriends>();

            CallbackManager = new CallbackManager(Client);

            new Callback<SteamClient.ConnectedCallback>(OnConnected, CallbackManager);
            new Callback<SteamClient.DisconnectedCallback>(OnDisconnected, CallbackManager);
            new Callback<SteamUser.AccountInfoCallback>(OnAccountInfo, CallbackManager);
            new Callback<SteamUser.LoggedOnCallback>(OnLoggedOn, CallbackManager);

            GameCoordinator = new GameCoordinator(AppID, Client, CallbackManager);
        }

        public void Run()
        {
            IsRunning = true;

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
				Log.WriteError(string.Format("GC {0}", AppID), "Could not connect: {0}", callback.Result);

				IsRunning = false;

				return;
            }

            Log.WriteInfo(string.Format("GC {0}", AppID), "Connected, logging in...");

            User.LogOn(new SteamUser.LogOnDetails
            {
                Username = Username,
                Password = Password
            });
        }

        private void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            if (!IsRunning)
            {
                Log.WriteInfo(string.Format("GC {0}", AppID), "Disconnected from Steam");
                return;
            }

            Log.WriteInfo(string.Format("GC {0}", AppID), "Disconnected from Steam. Retrying in 15 seconds...");

            Thread.Sleep(TimeSpan.FromSeconds(15));

            Client.Connect();
        }
    }
}
