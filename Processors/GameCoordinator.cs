/*
 * Copyright (c) 2013, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections.Generic;
using MySql.Data.MySqlClient;
using SteamKit2;
using SteamKit2.GC;
using SteamKit2.GC.Internal;
using SteamKit2.Internal;

namespace SteamDatabaseBackend
{
    public class GameCoordinator
    {
        private uint AppID;
        private SteamClient SteamClient;
        private SteamGameCoordinator SteamGameCoordinator;

        private Dictionary<uint, Action<IPacketGCMsg>> GCMessageMap;

        private System.Timers.Timer Timer;

        private uint LastVersion;
        private uint LastSchemaVersion;
        private GCConnectionStatus LastStatus = GCConnectionStatus.GCConnectionStatus_NO_STEAM;

        public GameCoordinator(uint appID, SteamClient steamClient, CallbackManager callbackManager)
        {
            // Map gc messages to our callback functions
            GCMessageMap = new Dictionary<uint, Action<IPacketGCMsg>>
            {
                { (uint)EGCBaseClientMsg.k_EMsgGCClientWelcome, OnClientWelcome },
                { (uint)EGCItemMsg.k_EMsgGCUpdateItemSchema, OnItemSchemaUpdate },
                { (uint)EGCBaseMsg.k_EMsgGCSystemMessage, OnSystemMessage },
                { (uint)EGCBaseClientMsg.k_EMsgGCClientConnectionStatus, OnClientConnectionStatus },
                { (uint)4008 /* TF2's k_EMsgGCClientGoodbye */, OnClientConnectionStatus },
                { (uint)EGCItemMsg.k_EMsgGCSaxxyBroadcast, OnSaxxyBroadcast }
            };

            this.AppID = appID;
            this.SteamClient = steamClient;
            this.SteamGameCoordinator = SteamClient.GetHandler<SteamGameCoordinator>();

            // Make sure Steam knows we're playing the game
            Timer = new System.Timers.Timer();
            Timer.Elapsed += new System.Timers.ElapsedEventHandler(OnTimerPlayGame);
            Timer.Interval = TimeSpan.FromMinutes(5).TotalMilliseconds;
            Timer.Start();

            Timer = new System.Timers.Timer();
            Timer.Elapsed += new System.Timers.ElapsedEventHandler(OnTimer);

            new Callback<SteamGameCoordinator.MessageCallback>(OnGameCoordinatorMessage, callbackManager);
        }

        private void OnTimerPlayGame(object sender, System.Timers.ElapsedEventArgs e)
        {
            PlayGame();
        }

        private void OnTimer(object sender, System.Timers.ElapsedEventArgs e)
        {
            Hello();
        }

        public void PlayGame()
        {
            var clientGamesPlayed = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayed);

            clientGamesPlayed.Body.games_played.Add(new CMsgClientGamesPlayed.GamePlayed
            {
                game_id = AppID
            });

            SteamClient.Send(clientGamesPlayed);
        }

        public void Hello()
        {
            var clientHello = new ClientGCMsgProtobuf<CMsgClientHello>((uint)EGCBaseClientMsg.k_EMsgGCClientHello);

            SteamGameCoordinator.Send(clientHello, AppID);
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
                Log.WriteDebug(string.Format("GC {0}", AppID), "Unhandled GC message - EMsg: {0} ({1})", callback.EMsg, GetEMsgDisplayString(callback.EMsg));
            }

            // If we hear from GC, but it's not hello, keep bugging it
            if (!Timer.Enabled && LastStatus == GCConnectionStatus.GCConnectionStatus_NO_SESSION)
            {
                Timer.Interval = TimeSpan.FromSeconds(60).TotalMilliseconds;
                Timer.Start();
            }
        }

        private void OnClientWelcome(IPacketGCMsg packetMsg)
        {
            Timer.Stop();

            var msg = new ClientGCMsgProtobuf<CMsgClientWelcome>(packetMsg).Body;

            Log.WriteInfo(string.Format("GC {0}", AppID), "New GC session ({0} -> {1})", LastVersion, msg.version);

            string message = string.Format("New {0}{1}{2} GC session", Colors.OLIVE, SteamProxy.GetAppName(AppID), Colors.NORMAL);

            if (LastVersion == 0 || LastVersion == msg.version)
            {
                message += string.Format(" {0}(version {1})", Colors.DARK_GRAY, msg.version);
            }
            else
            {
                message += string.Format(" {0}(version changed from {1} to {2})", Colors.DARK_GRAY, LastVersion, msg.version);
            }

            if (LastVersion != 0)
            {
                IRC.SendMain(message);
            }

            IRC.SendAnnounce(message);

            LastVersion = msg.version;
            LastStatus = GCConnectionStatus.GCConnectionStatus_HAVE_SESSION;

            DbWorker.ExecuteNonQuery("INSERT INTO `GC` (`AppID`, `Status`, `Version`) VALUES(@AppID, @Status, @Version) ON DUPLICATE KEY UPDATE `Status` = @Status, `Version` = @Version",
                                     new MySqlParameter("@AppID", AppID),
                                     new MySqlParameter("@Status", LastStatus.ToString()),
                                     new MySqlParameter("@Version", LastVersion)
            );
        }

        private void OnItemSchemaUpdate(IPacketGCMsg packetMsg)
        {
            var msg = new ClientGCMsgProtobuf<CMsgUpdateItemSchema>(packetMsg).Body;

            if (LastSchemaVersion != 0 && LastSchemaVersion != msg.item_schema_version)
            {
                Log.WriteInfo(string.Format("GC {0}", AppID), "Schema change from {0} to {1}", LastSchemaVersion, msg.item_schema_version);

                IRC.SendMain("{0}{1}{2} item schema updated: {3}{4}{5} -{6} {7}", Colors.OLIVE, SteamProxy.GetAppName(AppID), Colors.NORMAL, Colors.DARK_GRAY, msg.item_schema_version.ToString("X4"), Colors.NORMAL, Colors.DARK_BLUE, msg.items_game_url);
            }

            LastSchemaVersion = msg.item_schema_version;
        }

        private void OnSystemMessage(IPacketGCMsg packetMsg)
        {
            var msg = new ClientGCMsgProtobuf<CMsgSystemBroadcast>(packetMsg).Body;

            Log.WriteInfo(string.Format("GC {0}", AppID), "Message: {0}", msg.message);

            IRC.SendMain("{0}{1}{2} system message:{3} {4}", Colors.OLIVE, SteamProxy.GetAppName(AppID), Colors.NORMAL, Colors.OLIVE, msg.message);
        }

        private void OnClientConnectionStatus(IPacketGCMsg packetMsg)
        {
            var msg = new ClientGCMsgProtobuf<CMsgConnectionStatus>(packetMsg).Body;

            Log.WriteInfo(string.Format("GC {0}", AppID), "Status: {0}", msg.status);

            string message = string.Format("{0}{1}{2} GC status:{3} {4}", Colors.OLIVE, SteamProxy.GetAppName(AppID), Colors.NORMAL, Colors.OLIVE, msg.status);

            if (LastStatus != msg.status)
            {
                IRC.SendMain(message);

                LastStatus = msg.status;
            }

            IRC.SendAnnounce(message);

            if (LastStatus == GCConnectionStatus.GCConnectionStatus_NO_SESSION)
            {
                Timer.Interval = TimeSpan.FromSeconds(5).TotalMilliseconds;
                Timer.Start();
            }

            DbWorker.ExecuteNonQuery("INSERT INTO `GC` (`AppID`, `Status`) VALUES(@AppID, @Status) ON DUPLICATE KEY UPDATE `Status` = @Status",
                                     new MySqlParameter("@AppID", AppID),
                                     new MySqlParameter("@Status", LastStatus.ToString())
            );
        }

        private void OnSaxxyBroadcast(IPacketGCMsg packetMsg)
        {
            var msg = new ClientGCMsgProtobuf<SteamKit2.GC.TF2.Internal.CMsgTFSaxxyBroadcast>(packetMsg).Body;

            IRC.SendMain("{0}{1}{2} has won a saxxy in category {3}{4}", Colors.OLIVE, msg.user_name, Colors.NORMAL, Colors.OLIVE, msg.category_number);
        }

        private static string GetEMsgDisplayString(uint eMsg)
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
                if (Enum.IsDefined(enumType, (int)eMsg))
                {
                    return Enum.GetName(enumType, (int)eMsg);
                }
            }

            return eMsg.ToString();
        }
    }
}
