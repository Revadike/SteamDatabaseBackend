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
using SteamKit2.GC.CSGO.Internal;
using SteamKit2.GC.Dota.Internal;
using SteamKit2.GC.Internal;
using SteamKit2.Internal;

namespace SteamDatabaseBackend
{
    public class GameCoordinator
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
                { (uint)EGCBaseClientMsg.k_EMsgGCClientWelcome, OnClientWelcome },
                { (uint)EGCItemMsg.k_EMsgGCUpdateItemSchema, OnItemSchemaUpdate },
                { (uint)EGCBaseMsg.k_EMsgGCSystemMessage, OnSystemMessage },
                { (uint)EGCBaseClientMsg.k_EMsgGCClientConnectionStatus, OnClientConnectionStatus },
                { (uint)4008 /* TF2's k_EMsgGCClientGoodbye */, OnClientConnectionStatus }
            };

            this.AppID = appID;
            this.SteamClient = steamClient;
            this.SteamGameCoordinator = SteamClient.GetHandler<SteamGameCoordinator>();
            this.Name = string.Format("GC {0}", appID);

            // Make sure Steam knows we're playing the game
            Timer = new System.Timers.Timer();
            Timer.Elapsed += OnTimerPlayGame;
            Timer.Interval = TimeSpan.FromMinutes(5).TotalMilliseconds;
            Timer.Start();

            Timer = new System.Timers.Timer();
            Timer.Elapsed += OnTimer;

            callbackManager.Register(new Callback<SteamGameCoordinator.MessageCallback>(OnGameCoordinatorMessage));
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
                Log.WriteDebug(Name, "Unhandled GC message - EMsg: {0} ({1})", callback.EMsg, GetEMsgDisplayString((int)callback.EMsg));
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

            Log.WriteInfo(Name, "New GC session ({0} -> {1})", LastVersion, msg.version);

            string message = string.Format("New {0}{1}{2} GC session", Colors.OLIVE, SteamProxy.GetAppName(AppID), Colors.NORMAL);

            if (LastVersion == -1 || LastVersion == msg.version)
            {
                message += string.Format(" {0}(version {1})", Colors.DARK_GRAY, msg.version);
            }
            else
            {
                message += string.Format(" {0}(version changed from {1} to {2})", Colors.DARK_GRAY, LastVersion, msg.version);
            }

            if (LastVersion != -1 && (LastVersion != msg.version || LastStatus != GCConnectionStatus.GCConnectionStatus_HAVE_SESSION))
            {
                IRC.SendMain(message);
            }

            IRC.SendAnnounce(message);

            LastVersion = (int)msg.version;
            LastStatus = GCConnectionStatus.GCConnectionStatus_HAVE_SESSION;

            UpdateStatus(AppID, LastStatus.ToString());
        }

        private void OnItemSchemaUpdate(IPacketGCMsg packetMsg)
        {
            var msg = new ClientGCMsgProtobuf<CMsgUpdateItemSchema>(packetMsg).Body;

            if (LastSchemaVersion != 0 && LastSchemaVersion != msg.item_schema_version)
            {
                Log.WriteInfo(Name, "Schema change from {0} to {1}", LastSchemaVersion, msg.item_schema_version);

                IRC.SendMain("{0}{1}{2} item schema updated: {3}{4}{5} -{6} {7}", Colors.OLIVE, SteamProxy.GetAppName(AppID), Colors.NORMAL, Colors.DARK_GRAY, msg.item_schema_version.ToString("X4"), Colors.NORMAL, Colors.DARK_BLUE, msg.items_game_url);
            }

            LastSchemaVersion = msg.item_schema_version;
        }

        private void OnSystemMessage(IPacketGCMsg packetMsg)
        {
            var msg = new ClientGCMsgProtobuf<CMsgSystemBroadcast>(packetMsg).Body;

            Log.WriteInfo(Name, "Message: {0}", msg.message);

            IRC.SendMain("{0}{1}{2} system message:{3} {4}", Colors.OLIVE, SteamProxy.GetAppName(AppID), Colors.NORMAL, Colors.OLIVE, msg.message);
        }

        private void OnClientConnectionStatus(IPacketGCMsg packetMsg)
        {
            var msg = new ClientGCMsgProtobuf<CMsgConnectionStatus>(packetMsg).Body;

            Log.WriteInfo(Name, "Status: {0}", msg.status);

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
                typeof(EDOTAGCMsg),
                typeof(ECsgoGCMsg)
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
            DbWorker.ExecuteNonQuery("INSERT INTO `GC` (`AppID`, `Status`) VALUES(@AppID, @Status) ON DUPLICATE KEY UPDATE `Status` = @Status",
                                     new MySqlParameter("@AppID", appID),
                                     new MySqlParameter("@Status", status)
            );

            // We need to propagate Steam's status to main idler
            if (appID == 0 && Settings.Current.Steam.IdleAppID > 0)
            {
                UpdateStatus(Settings.Current.Steam.IdleAppID, status);
            }
        }
    }
}
