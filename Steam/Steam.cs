/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dapper;
using SteamKit2;
using SteamKit2.Discovery;

namespace SteamDatabaseBackend
{
    internal class Steam : IDisposable
    {
        public static Steam Instance { get; } = new Steam();
        public static SteamConfiguration Configuration { get; private set; }

        public SteamClient Client { get; }
        public SteamUser User { get; }
        public SteamApps Apps { get; }
        public SteamFriends Friends { get; }
        public SteamUserStats UserStats { get; }
        public SteamUnifiedMessages UnifiedMessages { get; }
        public CallbackManager CallbackManager { get; }

        public PICSChanges PICSChanges { get; }
        public DepotProcessor DepotProcessor { get; private set; }
        public FreeLicense FreeLicense { get; private set; }

        public bool IsRunning { get; set; }
        public bool IsLoggedOn => Client.SteamID != null;

        private readonly List<SteamHandler> Handlers;
        private Watchdog WatchdogHandle;

        private Steam()
        {
            Configuration = SteamConfiguration.Create(b => b
                .WithServerListProvider(new FileStorageServerListProvider(Path.Combine(Path.GetTempPath(), "steamdb_steamkit_servers.bin")))
                .WithProtocolTypes(ProtocolTypes.Tcp)
                .WithWebAPIKey(Settings.Current.Steam.WebAPIKey)
            );

            Client = new SteamClient(Configuration, "SteamDB");

#if DEBUG_NETHOOK
            Client.DebugNetworkListener = new NetHookNetworkListener();
#endif

            User = Client.GetHandler<SteamUser>();
            Apps = Client.GetHandler<SteamApps>();
            Friends = Client.GetHandler<SteamFriends>();
            UserStats = Client.GetHandler<SteamUserStats>();
            UnifiedMessages = Client.GetHandler<SteamUnifiedMessages>();

            CallbackManager = new CallbackManager(Client);

            Client.AddHandler(new PurchaseResponse());

            Handlers = new List<SteamHandler>
            {
                new Connection(CallbackManager),
                new AccountInfo(CallbackManager),
                new PICSProductInfo(CallbackManager),
                new PICSTokens(CallbackManager),
                new LicenseList(CallbackManager),
                new WebAuth(CallbackManager)
            };

            if (!Settings.IsFullRun)
            {
                Handlers.Add(new ClanState(CallbackManager));
            }

            FreeLicense = new FreeLicense(CallbackManager);
            PICSChanges = new PICSChanges(CallbackManager);
            DepotProcessor = new DepotProcessor(Client);
            WatchdogHandle = new Watchdog();

            IsRunning = true;
        }

        public void Dispose()
        {
            if (DepotProcessor != null)
            {
                DepotProcessor.Dispose();
                DepotProcessor = null;
            }

            if (WatchdogHandle != null)
            {
                WatchdogHandle.Dispose();
                WatchdogHandle = null;
            }

            if (FreeLicense != null)
            {
                FreeLicense.Dispose();
                FreeLicense = null;
            }
        }

        public void Tick()
        {
            Client.Connect();

            while (IsRunning)
            {
                CallbackManager.RunWaitCallbacks();
            }
        }

        public void RegisterCommandHandlers(CommandHandler handler)
        {
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

        public static string GetAppName(uint appID, out EAppType appType)
        {
            App data;

            using (var db = Database.Get())
            {
                data = db.Query<App>("SELECT `AppID`, `Apps`.`Name`, `LastKnownName`, `Apps`.`AppType` FROM `Apps` WHERE `AppID` = @AppID", new { AppID = appID }).SingleOrDefault();
            }

            appType = data.AppID == 0 || data.AppType == EAppType.Invalid ? EAppType.Application : data.AppType;

            return FormatAppName(appID, data);
        }

        public static string FormatAppName(uint appID, App data)
        {
            if (data.AppID == 0)
            {
                return $"AppID {appID}";
            }

            var name = Utils.RemoveControlCharacters(data.Name);
            var nameLast = Utils.RemoveControlCharacters(data.LastKnownName);

            if (!string.IsNullOrEmpty(nameLast) && !name.Equals(nameLast, StringComparison.CurrentCultureIgnoreCase))
            {
                return $"{Utils.LimitStringLength(name)} {Colors.DARKGRAY}({Utils.LimitStringLength(nameLast)}){Colors.NORMAL}";
            }

            return Utils.LimitStringLength(name);
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

                return $"SubID {subID}";
            }

            var name = Utils.RemoveControlCharacters(data.Name);
            var nameLast = Utils.RemoveControlCharacters(data.LastKnownName);

            if (string.IsNullOrEmpty(nameLast))
            {
                return Utils.LimitStringLength(name);
            }

            if (!name.Equals(nameLast, StringComparison.CurrentCultureIgnoreCase) && !name.StartsWith("Steam Sub ", StringComparison.Ordinal))
            {
                return $"{Utils.LimitStringLength(nameLast)} {Colors.DARKGRAY}({Utils.LimitStringLength(name)}){Colors.NORMAL}";
            }

            return Utils.LimitStringLength(nameLast);
        }
    }
}
