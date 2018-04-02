/*
 * Copyright (c) 2013-2018, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.IO;
using System.Linq;
using Dapper;
using SteamKit2;
using SteamKit2.Discovery;

namespace SteamDatabaseBackend
{
    class Steam
    {
        public static Steam Instance { get; } = new Steam();
        public static SteamAnonymous Anonymous { get; } = new SteamAnonymous();
        public static SteamConfiguration Configuration { get; private set; }
        
        public SteamClient Client { get; }
        public SteamUser User { get; }
        public SteamApps Apps { get; }
        public SteamFriends Friends { get; }
        public SteamUserStats UserStats { get; }
        public CallbackManager CallbackManager { get; }

        public PICSChanges PICSChanges { get; }
        public DepotProcessor DepotProcessor { get; }

        public bool IsRunning { get; set; }

        private Steam()
        {
            Configuration = SteamConfiguration.Create(b => b
                .WithServerListProvider(new FileStorageServerListProvider(Path.Combine(Application.Path, "files", ".support", "servers.bin")))
                .WithCellID(LocalConfig.CellID)
                .WithProtocolTypes(ProtocolTypes.Tcp)
                .WithWebAPIBaseAddress(Settings.Current.Steam.WebAPIUrl)
                .WithWebAPIKey(Settings.Current.Steam.WebAPIKey)
            );

            Client = new SteamClient(Configuration);

            User = Client.GetHandler<SteamUser>();
            Apps = Client.GetHandler<SteamApps>();
            Friends = Client.GetHandler<SteamFriends>();
            UserStats = Client.GetHandler<SteamUserStats>();

            CallbackManager = new CallbackManager(Client);

            Client.AddHandler(new PurchaseResponse());

            new Connection(CallbackManager);
            new AccountInfo(CallbackManager);
            new PICSProductInfo(CallbackManager);
            new PICSTokens(CallbackManager);
            new LicenseList(CallbackManager);
            new WebAuth(CallbackManager);
            new FreeLicense(CallbackManager);

            if (!Settings.IsFullRun)
            {
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
                CallbackManager.Subscribe<SteamFriends.ChatMsgCallback>(handler.OnSteamChatMessage);
            }

            CallbackManager.Subscribe<SteamFriends.FriendMsgCallback>(handler.OnSteamFriendMessage);
        }

        public static string GetPackageName(uint subID)
        {
            Package data;

            using (var db = Database.Get())
            {
                data = db.Query<Package>("SELECT `SubID`, `Name`, `LastKnownName` FROM `Subs` WHERE `SubID` = @SubID", new { SubID = subID }).FirstOrDefault();
            }

            return FormatPackageName(subID, data);
        }

        public static string GetAppName(uint appID)
        {
            App data;

            using (var db = Database.Get())
            {
                data = db.Query<App>("SELECT `AppID`, `Name`, `LastKnownName` FROM `Apps` WHERE `AppID` = @AppID", new { AppID = appID }).SingleOrDefault();
            }

            return FormatAppName(appID, data);
        }

        public static string GetAppName(uint appID, out string appType)
        {
            App data;

            using (var db = Database.Get())
            {
                data = db.Query<App>("SELECT `AppID`, `Apps`.`Name`, `LastKnownName`, `Apps`.`AppType`, `AppsTypes`.`DisplayName` as `AppTypeString` FROM `Apps` LEFT JOIN `AppsTypes` ON `Apps`.`AppType` = `AppsTypes`.`AppType` WHERE `AppID` = @AppID", new { AppID = appID }).SingleOrDefault();
            }

            appType = data.AppID == 0 || data.AppType == 0 ? "App" : data.AppTypeString;

            return FormatAppName(appID, data);
        }

        public static string FormatAppName(uint appID, App data)
        {
            if (data.AppID == 0)
            {
                return string.Format("AppID {0}", appID);
            }

            string name     = Utils.RemoveControlCharacters(data.Name);
            string nameLast = Utils.RemoveControlCharacters(data.LastKnownName);

            if (!string.IsNullOrEmpty(nameLast) && !name.Equals(nameLast, StringComparison.CurrentCultureIgnoreCase))
            {
                return string.Format("{0} {1}({2}){3}", name, Colors.DARKGRAY, nameLast, Colors.NORMAL);
            }

            return name;
        }

        public static string FormatPackageName(uint subID, Package data)
        {
            if (data.SubID == 0)
            {
                // much hackery
                if (subID == 0)
                {
                    return "Steam";
                }

                return string.Format("SubID {0}", subID);
            }

            string name     = Utils.RemoveControlCharacters(data.Name);
            string nameLast = Utils.RemoveControlCharacters(data.LastKnownName);

            if (string.IsNullOrEmpty(nameLast))
            {
                return name;
            }

            if (!name.Equals(nameLast, StringComparison.CurrentCultureIgnoreCase) && !name.StartsWith("Steam Sub ", StringComparison.Ordinal))
            {
                return string.Format("{0} {1}({2}){3}", nameLast, Colors.DARKGRAY, name, Colors.NORMAL);
            }

            return nameLast;
        }
    }
}
