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
        public bool IsRunning { get; set; }

        public SteamClient Client { get; private set; }
        private SteamGameServer User;
        private CallbackManager CallbackManager;
        private GameCoordinator GameCoordinator;

        private uint AppID;

        public GCIdler(uint appID)
        {
            AppID = appID;
            
            Client = new SteamClient();

            User = Client.GetHandler<SteamGameServer>();

            CallbackManager = new CallbackManager(Client);

            CallbackManager.Register(new Callback<SteamClient.ConnectedCallback>(OnConnected));
            CallbackManager.Register(new Callback<SteamClient.DisconnectedCallback>(OnDisconnected));
            CallbackManager.Register(new Callback<SteamUser.LoggedOnCallback>(OnLoggedOn));
            CallbackManager.Register(new Callback<SteamUser.LoggedOffCallback>(OnLoggedOff));

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
                GameCoordinator.UpdateStatus(AppID, "Launching");

                User.SendStatus(new SteamGameServer.StatusDetails
                {
                    AppID = AppID,
                    Port = 27015,
                    QueryPort = 27015,
                    ServerFlags = EServerFlags.Private | EServerFlags.Passworded
                });

                // TF2 GC will happily greet us
                if (AppID != 440)
                {
                    GameCoordinator.Hello();
                }
            }
            else
            {
                GameCoordinator.UpdateStatus(AppID, callback.Result.ToString());
            }
        }

        private void OnLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            GameCoordinator.UpdateStatus(AppID, EResult.NotLoggedOn.ToString());
        }

        private void OnConnected(SteamClient.ConnectedCallback callback)
        {
            if (callback.Result != EResult.OK)
            {
                GameCoordinator.UpdateStatus(AppID, callback.Result.ToString());

                Log.WriteError(string.Format("GC {0}", AppID), "Could not connect: {0}", callback.Result);

                IsRunning = false;

                return;
            }

            GameCoordinator.UpdateStatus(AppID, EResult.NotLoggedOn.ToString());

            Log.WriteInfo(string.Format("GC {0}", AppID), "Connected, logging in...");

            User.LogOnAnonymous(AppID);
        }

        private void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            if (!IsRunning)
            {
                Log.WriteInfo(string.Format("GC {0}", AppID), "Disconnected from Steam");
                return;
            }

            GameCoordinator.UpdateStatus(AppID, EResult.NoConnection.ToString());

            Log.WriteInfo(string.Format("GC {0}", AppID), "Disconnected from Steam. Retrying in 15 seconds...");

            Thread.Sleep(TimeSpan.FromSeconds(15));

            Client.Connect();
        }
    }
}
