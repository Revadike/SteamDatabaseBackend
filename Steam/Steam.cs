/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using Dapper;
using SteamKit2;

namespace SteamDatabaseBackend
{
    class Steam
    {
        private static Steam _instance = new Steam();
        public static Steam Instance { get { return _instance; } }

        public SteamClient Client { get; private set; }
        public SteamUser User { get; private set; }
        public SteamApps Apps { get; private set; }
        public SteamFriends Friends { get; private set; }
        public SteamUserStats UserStats { get; private set; }
        public CallbackManager CallbackManager { get; private set; }

        public PICSChanges PICSChanges { get; private set; }
        public DepotProcessor DepotProcessor { get; private set; }

        public bool IsRunning { get; set; }

        public Steam()
        {
            Client = new SteamClient();

            User = Client.GetHandler<SteamUser>();
            Apps = Client.GetHandler<SteamApps>();
            Friends = Client.GetHandler<SteamFriends>();
            UserStats = Client.GetHandler<SteamUserStats>();

            CallbackManager = new CallbackManager(Client);

            Client.AddHandler(new FreeLicense());

            new Connection(CallbackManager);
            new PICSProductInfo(CallbackManager);
            new PICSTokens(CallbackManager);
            new LicenseList(CallbackManager);

            if (!Settings.IsFullRun)
            {
                new AccountInfo(CallbackManager);
                new MarketingMessage(CallbackManager);
                new ClanState(CallbackManager);
                new ChatMemberInfo(CallbackManager);

                new GameCoordinator(Client, CallbackManager);

                new Watchdog();
            }

            PICSChanges = new PICSChanges(CallbackManager);
            DepotProcessor = new DepotProcessor(Client, CallbackManager);

            IsRunning = true;
        }

        public void Tick()
        {
            Client.Connect();

            while (IsRunning)
            {
                CallbackManager.RunWaitCallbacks(TimeSpan.FromSeconds(1));
            }
        }

        public void RegisterCommandHandlers(CommandHandler handler)
        {
            if (Settings.Current.ChatRooms.Count > 0)
            {
                CallbackManager.Register(new Callback<SteamFriends.ChatMsgCallback>(handler.OnSteamChatMessage));
            }

            CallbackManager.Register(new Callback<SteamFriends.FriendMsgCallback>(handler.OnSteamFriendMessage));
        }

        public static string GetPackageName(uint subID)
        {
            Package? data;

            using (var db = Database.GetConnection())
            {
                data = db.ExecuteScalar<Package?>("SELECT `Name`, `LastKnownName` FROM `Subs` WHERE `SubID` = @SubID", new { SubID = subID });
            }

            if (!data.HasValue)
            {
                return string.Format("SubID {0}", subID);
            }

            string name     = Utils.RemoveControlCharacters(data.Value.Name);
            string nameLast = Utils.RemoveControlCharacters(data.Value.LastKnownName);

            if (!string.IsNullOrEmpty(nameLast) && !name.Equals(nameLast)) // TODO: Only do it for 'Steam Sub' names?
            {
                return string.Format("{0} {1}({2}){3}", name, Colors.DARKGRAY, nameLast, Colors.NORMAL);
            }

            return name;
        }

        public static string GetAppName(uint appID)
        {
            App? data;

            using (var db = Database.GetConnection())
            {
                data = db.ExecuteScalar<App?>("SELECT `Name`, `LastKnownName` FROM `Apps` WHERE `AppID` = @AppID", new { AppID = appID });
            }

            if (!data.HasValue)
            {
                return string.Format("AppID {0}", appID);
            }

            string name     = Utils.RemoveControlCharacters(data.Value.Name);
            string nameLast = Utils.RemoveControlCharacters(data.Value.LastKnownName);

            if (!string.IsNullOrEmpty(nameLast) && !name.Equals(nameLast))
            {
                return string.Format("{0} {1}({2}){3}", name, Colors.DARKGRAY, nameLast, Colors.NORMAL);
            }

            return name;
        }
    }
}
