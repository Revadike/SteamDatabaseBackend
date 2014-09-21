/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections.Generic;
using MySql.Data.MySqlClient;
using SteamKit2;
using SteamKit2.GC;
using SteamKit2.GC.Internal;

namespace SteamDatabaseBackend
{
    class GameCoordinator
    {
        private readonly uint AppID;
        private readonly SteamClient SteamClient;
        private readonly SteamGameCoordinator SteamGameCoordinator;
        private readonly Dictionary<uint, Action<IPacketGCMsg>> GCMessageMap;
        private readonly System.Timers.Timer Timer;
        private readonly string Name;
        private int LastVersion = -1;
        private uint LastSchemaVersion;
        private GCConnectionStatus LastStatus = GCConnectionStatus.GCConnectionStatus_NO_STEAM;

        public GameCoordinator(uint appID, SteamClient steamClient, CallbackManager callbackManager)
        {
            // Map gc messages to our callback functions
            GCMessageMap = new Dictionary<uint, Action<IPacketGCMsg>>
            {
                { (uint)4008 /* TF2's k_EMsgGCClientGoodbye */, OnConnectionStatus },
                { (uint)EGCBaseClientMsg.k_EMsgGCServerConnectionStatus, OnConnectionStatus },
                { (uint)EGCBaseClientMsg.k_EMsgGCServerWelcome, OnWelcome },
                { (uint)EGCItemMsg.k_EMsgGCUpdateItemSchema, OnItemSchemaUpdate },
                { (uint)EGCItemMsg.k_EMsgGCServerVersionUpdated, OnVersionUpdate },
                { (uint)EGCBaseMsg.k_EMsgGCSystemMessage, OnSystemMessage }
            };

            this.AppID = appID;
            this.SteamClient = steamClient;
            this.SteamGameCoordinator = SteamClient.GetHandler<SteamGameCoordinator>();
            this.Name = string.Format("GC {0}", appID);

            Timer = new System.Timers.Timer();
            Timer.Elapsed += OnTimer;

            callbackManager.Register(new Callback<SteamGameCoordinator.MessageCallback>(OnGameCoordinatorMessage));
        }

        public void Login()
        {
            // TF2 GC should greet us on its own
            if (AppID != 440)
            {
                Hello();
            }

            Timer.Interval = TimeSpan.FromSeconds(30).TotalMilliseconds;
            Timer.Start();
        }

        private void OnTimer(object sender, System.Timers.ElapsedEventArgs e)
        {
            Hello();
        }

        private void Hello()
        {
            var serverHello = new ClientGCMsgProtobuf<CMsgClientHello>((uint)EGCBaseClientMsg.k_EMsgGCServerHello);

            SteamGameCoordinator.Send(serverHello, AppID);
        }

        private void OnGameCoordinatorMessage(SteamGameCoordinator.MessageCallback callback)
        {
            Action<IPacketGCMsg> callbackFunction;

            if (GCMessageMap.TryGetValue(callback.EMsg, out callbackFunction))
            {
                callbackFunction(callback.Message);
            }
            else
            {
                Log.WriteDebug(Name, "Unhandled GC message - EMsg: {0} ({1})", callback.EMsg, GetEMsgDisplayString((int)callback.EMsg));
            }

            // If we hear from GC, but it's not hello, keep bugging it
            if (!Timer.Enabled && LastStatus == GCConnectionStatus.GCConnectionStatus_NO_SESSION)
            {
                Timer.Interval = TimeSpan.FromSeconds(60).TotalMilliseconds;
                Timer.Start();
            }
        }

        private void OnWelcome(IPacketGCMsg packetMsg)
        {
            Timer.Stop();

            int version = -1;

            // TF2 GC is not in sync
            if (AppID == 440)
            {
                var msg = new ClientGCMsgProtobuf<SteamKit2.GC.TF2.Internal.CMsgServerWelcome>(packetMsg).Body;

                version = (int)msg.active_version;
            }
            else
            {
                var msg = new ClientGCMsgProtobuf<CMsgClientWelcome>(packetMsg).Body;

                version = (int)msg.version;
            }

            Log.WriteInfo(Name, "New GC session ({0} -> {1})", LastVersion, version);

            string message = string.Format("New {0}{1}{2} GC session", Colors.BLUE, Steam.GetAppName(AppID), Colors.NORMAL);

            if (LastVersion == -1 || LastVersion == version)
            {
                message += string.Format(" {0}(version {1})", Colors.DARKGRAY, version);
            }
            else
            {
                message += string.Format(" {0}(version changed from {1} to {2})", Colors.DARKGRAY, LastVersion, version);
            }

            if (LastVersion != -1 && (LastVersion != version || LastStatus != GCConnectionStatus.GCConnectionStatus_HAVE_SESSION))
            {
                IRC.Instance.SendMain(message);
            }

            IRC.Instance.SendAnnounce(message);

            LastVersion = version;
            LastStatus = GCConnectionStatus.GCConnectionStatus_HAVE_SESSION;

            UpdateStatus(AppID, LastStatus.ToString());
        }

        private void OnItemSchemaUpdate(IPacketGCMsg packetMsg)
        {
            var msg = new ClientGCMsgProtobuf<CMsgUpdateItemSchema>(packetMsg).Body;

            if (LastSchemaVersion != 0 && LastSchemaVersion != msg.item_schema_version)
            {
                Log.WriteInfo(Name, "Schema change from {0} to {1}", LastSchemaVersion, msg.item_schema_version);

                IRC.Instance.SendMain("{0}{1}{2} item schema updated: {3}{4}{5} -{6} {7}", Colors.BLUE, Steam.GetAppName(AppID), Colors.NORMAL, Colors.DARKGRAY, msg.item_schema_version.ToString("X4"), Colors.NORMAL, Colors.DARKBLUE, msg.items_game_url);
            }

            LastSchemaVersion = msg.item_schema_version;

#if DEBUG
            Log.WriteDebug(Name, msg.items_game_url);
#endif
        }

        private void OnVersionUpdate(IPacketGCMsg packetMsg)
        {
            var msg = new ClientGCMsgProtobuf<CMsgGCServerVersionUpdated>(packetMsg).Body;

            Log.WriteInfo(Name, "GC version changed ({0} -> {1})", LastVersion, msg.server_version);

            IRC.Instance.SendMain("{0}{1}{2} server version changed:{3} {4} {5}(from {6})", Colors.BLUE, Steam.GetAppName(AppID), Colors.NORMAL, Colors.BLUE, msg.server_version, Colors.DARKGRAY, LastVersion);

            LastVersion = (int)msg.server_version;
        }

        private void OnSystemMessage(IPacketGCMsg packetMsg)
        {
            var msg = new ClientGCMsgProtobuf<CMsgSystemBroadcast>(packetMsg).Body;

            Log.WriteInfo(Name, "Message: {0}", msg.message);

            IRC.Instance.SendMain("{0}{1}{2} system message:{3} {4}", Colors.BLUE, Steam.GetAppName(AppID), Colors.NORMAL, Colors.OLIVE, msg.message);
        }

        private void OnConnectionStatus(IPacketGCMsg packetMsg)
        {
            var msg = new ClientGCMsgProtobuf<CMsgConnectionStatus>(packetMsg).Body;

            LastStatus = msg.status;

            Log.WriteInfo(Name, "Status: {0}", LastStatus);

            string message = string.Format("{0}{1}{2} GC status:{3} {4}", Colors.BLUE, Steam.GetAppName(AppID), Colors.NORMAL, Colors.OLIVE, LastStatus);

            IRC.Instance.SendAnnounce(message);

            if (LastStatus == GCConnectionStatus.GCConnectionStatus_NO_SESSION)
            {
                Timer.Interval = TimeSpan.FromSeconds(5).TotalMilliseconds;
                Timer.Start();
            }

            UpdateStatus(AppID, LastStatus.ToString());
        }

        private static string GetEMsgDisplayString(int eMsg)
        {
            Type[] eMsgEnums =
            {
                typeof(EGCBaseClientMsg),
                typeof(EGCBaseMsg),
                typeof(EGCItemMsg),
                typeof(ESOMsg),
                typeof(EGCSystemMsg),
                typeof(SteamKit2.GC.Dota.Internal.EDOTAGCMsg),
                typeof(SteamKit2.GC.CSGO.Internal.ECsgoGCMsg)
            };

            foreach (var enumType in eMsgEnums)
            {
                if (Enum.IsDefined(enumType, eMsg))
                {
                    return Enum.GetName(enumType, eMsg);
                }
            }

            return eMsg.ToString();
        }

        public static void UpdateStatus(uint appID, string status)
        {
            DbWorker.ExecuteNonQuery(
                "INSERT INTO `GC` (`AppID`, `Status`) VALUES(@AppID, @Status) ON DUPLICATE KEY UPDATE `Status` = @Status",
                new MySqlParameter("@AppID", appID),
                new MySqlParameter("@Status", status)
            );
        }
    }
}
