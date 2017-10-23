/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Timers;
using Dapper;
using SteamKit2;
using SteamKit2.GC;
using SteamKit2.GC.Dota.Internal;

// TF2
using CMsgGCTFSpecificItemBroadcast = SteamKit2.GC.TF2.Internal.CMsgGCTFSpecificItemBroadcast;
using CMsgTFGoldenWrenchBroadcast   = SteamKit2.GC.TF2.Internal.CMsgTFGoldenWrenchBroadcast;

namespace SteamDatabaseBackend
{
    class GameCoordinator
    {
        class SessionInfo
        {
            public uint AppID { get; set; }
            public uint Version { get; set; }
            public uint SchemaVersion { get; set; }

            private GCConnectionStatus _status;
            public GCConnectionStatus Status
            {
                get => _status;
                set
                {
                    if (value != _status)
                    {
                        _status = value;

                        UpdateStatus(AppID, _status.ToString());
                    }
                }
            }
        }

        const uint k_EMsgGCClientGoodbye = 4008;
        const uint k_EMsgGCTFSpecificItemBroadcast = 1096;

        private readonly SteamGameCoordinator SteamGameCoordinator;
        private readonly Dictionary<uint, SessionInfo> SessionMap;
        private readonly Dictionary<uint, Action<uint, IPacketGCMsg>> MessageMap;
        private readonly Timer SessionTimer;

        public GameCoordinator(SteamClient steamClient, CallbackManager manager)
        {
            SessionMap = new Dictionary<uint, SessionInfo>();

            // Map gc messages to our callback functions
            MessageMap = new Dictionary<uint, Action<uint, IPacketGCMsg>>
            {
                { (uint)EGCBaseClientMsg.k_EMsgGCClientConnectionStatus, OnConnectionStatus },
                { (uint)EGCBaseClientMsg.k_EMsgGCClientWelcome, OnWelcome },
                { (uint)EGCItemMsg.k_EMsgGCUpdateItemSchema, OnItemSchemaUpdate },
                { (uint)EGCItemMsg.k_EMsgGCClientVersionUpdated, OnVersionUpdate },
                { (uint)EGCBaseMsg.k_EMsgGCSystemMessage, OnSystemMessage },

                // TF2 specific messages
                { k_EMsgGCClientGoodbye, OnConnectionStatus },
                { (uint)EGCItemMsg.k_EMsgGCGoldenWrenchBroadcast, OnWrenchBroadcast },
                { k_EMsgGCTFSpecificItemBroadcast, OnItemBroadcast },
            };

            SteamGameCoordinator = steamClient.GetHandler<SteamGameCoordinator>();

            SessionTimer = new Timer
            {
                Interval = TimeSpan.FromSeconds(30).TotalMilliseconds
            };
            SessionTimer.Elapsed += OnSessionTick;
            SessionTimer.Start();

            manager.Subscribe<SteamGameCoordinator.MessageCallback>(OnGameCoordinatorMessage);
            manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
            manager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
            manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
        }

        private void OnSessionTick(object sender, ElapsedEventArgs e)
        {
            if (!Steam.Instance.Client.IsConnected)
            {
                return;
            }

            foreach (var appID in Settings.Current.GameCoordinatorIdlers)
            {
                var info = GetSessionInfo(appID);

                if (info.Status == GCConnectionStatus.GCConnectionStatus_NO_SESSION
                ||  info.Status == GCConnectionStatus.GCConnectionStatus_GC_GOING_DOWN)
                {
                    var hello = new ClientGCMsgProtobuf<CMsgClientHello>((uint)EGCBaseClientMsg.k_EMsgGCClientHello);

                    if (info.AppID == 570)
                    {
                        hello.Body.engine = ESourceEngine.k_ESE_Source2;
                    }

                    SteamGameCoordinator.Send(hello, appID);
                }
            }
        }

        private void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            ResetSessions();
        }

        private void OnLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            ResetSessions();
        }

        private void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            OnSessionTick(null, null);
        }

        private void ResetSessions()
        {
            foreach (var appID in Settings.Current.GameCoordinatorIdlers)
            {
                GetSessionInfo(appID).Status = GCConnectionStatus.GCConnectionStatus_NO_SESSION;
            }
        }

        private void OnGameCoordinatorMessage(SteamGameCoordinator.MessageCallback callback)
        {
            Action<uint, IPacketGCMsg> callbackFunction;

            if (MessageMap.TryGetValue(callback.EMsg, out callbackFunction))
            {
                callbackFunction(callback.AppID, callback.Message);
            }
        }

        private void OnWelcome(uint appID, IPacketGCMsg packetMsg)
        {
            uint version;

            switch (appID)
            {
                case 730:
                    version = new ClientGCMsgProtobuf<SteamKit2.GC.CSGO.Internal.CMsgClientWelcome>(packetMsg).Body.version;
                    break;

                case 440:
                    version = new ClientGCMsgProtobuf<SteamKit2.GC.TF2.Internal.CMsgClientWelcome>(packetMsg).Body.version;
                    break;

                default:
                    version = new ClientGCMsgProtobuf<CMsgClientWelcome>(packetMsg).Body.version;
                    break;
            }

            var info = GetSessionInfo(appID);

            string message = string.Format("{0}{1}{2} new GC session", Colors.BLUE, Steam.GetAppName(appID), Colors.NORMAL);

            if (info.Version == 0 || info.Version == version)
            {
                message += string.Format(" {0}(version {1})", Colors.DARKGRAY, version);
            }
            else
            {
                message += string.Format(" {0}(version changed from {1} to {2})", Colors.DARKGRAY, info.Version, version);
            }

            IRC.Instance.SendAnnounce(message);

            info.Version = version;
            info.Status = GCConnectionStatus.GCConnectionStatus_HAVE_SESSION;
        }

        private void OnItemSchemaUpdate(uint appID, IPacketGCMsg packetMsg)
        {
            var msg = new ClientGCMsgProtobuf<CMsgUpdateItemSchema>(packetMsg).Body;

            var info = GetSessionInfo(appID);

            if (info.SchemaVersion != 0 && info.SchemaVersion != msg.item_schema_version)
            {
                var message = string.Format(
                    "{0}{1}{2} item schema updated: {3}{4}{5} -{6} {7}",
                    Colors.BLUE, Steam.GetAppName(appID), Colors.NORMAL, Colors.DARKGRAY, msg.item_schema_version.ToString("X4"), Colors.NORMAL, Colors.DARKBLUE, msg.items_game_url
                );

                IRC.Instance.SendMain(message);
                IRC.Instance.SendAnnounce(message);
            }

            info.SchemaVersion = msg.item_schema_version;
        }

        private void OnVersionUpdate(uint appID, IPacketGCMsg packetMsg)
        {
            var msg = new ClientGCMsgProtobuf<CMsgGCClientVersionUpdated>(packetMsg).Body;

            var info = GetSessionInfo(appID);

            IRC.Instance.SendAnnounce("{0}{1}{2} client version changed:{3} {4} {5}(from {6})", Colors.BLUE, Steam.GetAppName(appID), Colors.NORMAL, Colors.BLUE, msg.client_version, Colors.DARKGRAY, info.Version);

            info.Version = msg.client_version;
        }

        private void OnSystemMessage(uint appID, IPacketGCMsg packetMsg)
        {
            var msg = new ClientGCMsgProtobuf<CMsgSystemBroadcast>(packetMsg).Body;

            var message = string.Format("{0}{1}{2} system message:{3} {4}", Colors.BLUE, Steam.GetAppName(appID), Colors.NORMAL, Colors.OLIVE, msg.message);

            IRC.Instance.SendMain(message);
            IRC.Instance.SendAnnounce(message);
        }

        private void OnConnectionStatus(uint appID, IPacketGCMsg packetMsg)
        {
            var msg = new ClientGCMsgProtobuf<CMsgConnectionStatus>(packetMsg).Body;

            GetSessionInfo(appID).Status = msg.status;

            string extraInfo = string.Empty;

            if (msg.status == GCConnectionStatus.GCConnectionStatus_NO_SESSION_IN_LOGON_QUEUE)
            {
                extraInfo = string.Format(" {0}(queue: {1}/{2}, waited {3} of an estimated {4} seconds)",
                    Colors.DARKGRAY, msg.queue_position, msg.queue_size, msg.wait_seconds, msg.estimated_wait_seconds_remaining
                );
            }

            IRC.Instance.SendAnnounce("{0}{1}{2} status:{3} {4}{5}", Colors.BLUE, Steam.GetAppName(appID), Colors.NORMAL, Colors.OLIVE, msg.status, extraInfo);
        }

        private void OnWrenchBroadcast(uint appID, IPacketGCMsg packetMsg)
        {
            if (appID != 440)
            {
                // This message should be TF2 specific, but just in case
                return;
            }

            var msg = new ClientGCMsgProtobuf<CMsgTFGoldenWrenchBroadcast>(packetMsg).Body;

            var message = string.Format("{0}{1}{2} item notification: {3}{4}{5} has {6} Golden Wrench no. {7}{8}{9}!",
                Colors.BLUE, Steam.GetAppName(appID), Colors.NORMAL,
                Colors.BLUE, msg.user_name, Colors.NORMAL,
                msg.deleted ? "destroyed" : "found",
                Colors.OLIVE, msg.wrench_number, Colors.NORMAL
            );

            IRC.Instance.SendMain(message);
            IRC.Instance.SendAnnounce(message);
        }

        private void OnItemBroadcast(uint appID, IPacketGCMsg packetMsg)
        {
            if (appID != 440)
            {
                // This message should be TF2 specific, but just in case
                return;
            }

            var msg = new ClientGCMsgProtobuf<CMsgGCTFSpecificItemBroadcast>(packetMsg).Body;

            var message = string.Format("{0}{1}{2} item notification: {3}{4}{5} {6} {7}{8}{9}!",
                Colors.BLUE, Steam.GetAppName(appID), Colors.NORMAL,
                Colors.BLUE, msg.user_name, Colors.NORMAL,
                msg.was_destruction ? "has destroyed their" : "just received a",
                Colors.OLIVE, GetTF2ItemName(msg.item_def_index), Colors.NORMAL
            );

            IRC.Instance.SendMain(message);
            IRC.Instance.SendAnnounce(message);
        }

        private SessionInfo GetSessionInfo(uint appID)
        {
            SessionInfo info;

            if (SessionMap.TryGetValue(appID, out info))
            {
                return info;
            }

            info = new SessionInfo
            {
                AppID = appID,
                Status = GCConnectionStatus.GCConnectionStatus_NO_SESSION
            };

            SessionMap.Add(appID, info);

            return info;
        }

        private static string GetTF2ItemName(uint itemDefIndex)
        {
            var file = Path.Combine(Application.Path, "files", "tf", "tf", "scripts", "items", "items_game.txt");

            var schema = KeyValue.LoadAsText(file);

            string itemName = null;

            if (schema == null)
            {
                Log.WriteWarn("Game Coordinator", "Unable to load TF2 item schema");
            }
            else
            {
                itemName = schema["items"][itemDefIndex.ToString()]["item_name"].AsString();

                file = Path.Combine(Application.Path, "files", "tf", "tf", "resource", "tf_english.txt");

                schema = KeyValue.LoadAsText(file);

                if (schema == null)
                {
                    Log.WriteWarn("Game Coordinator", "Unable to load TF2 language");
                }
                else
                {
                    var token = schema["lang"]["Tokens"][itemName.TrimStart('#')];

                    if (token != null)
                    {
                        itemName = token.AsString();
                    }
                }
            }

            return itemName ?? string.Format("Item #{0}", itemDefIndex);
        }

        public static void UpdateStatus(uint appID, string status)
        {
            using (var db = Database.Get())
            {
                db.Execute("INSERT INTO `GC` (`AppID`, `Status`) VALUES(@AppID, @Status) ON DUPLICATE KEY UPDATE `Status` = VALUES(`Status`)", new { AppID = appID, Status = status });
            }
        }
    }
}
