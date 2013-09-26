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

        private System.Timers.Timer Timer;

        private uint LastVersion;
        private uint LastSchemaVersion;
        private GCConnectionStatus LastStatus = GCConnectionStatus.GCConnectionStatus_NO_STEAM;

        public GameCoordinator(uint AppID, SteamClient SteamClient, CallbackManager CallbackManager)
        {
            this.AppID = AppID;
            this.SteamClient = SteamClient;
            this.SteamGameCoordinator = SteamClient.GetHandler<SteamGameCoordinator>();

            // Make sure Steam knows we're playing the game
            Timer = new System.Timers.Timer();
            Timer.Elapsed += new System.Timers.ElapsedEventHandler(OnTimerPlayGame);
            Timer.Interval = TimeSpan.FromMinutes(5).TotalMilliseconds;
            Timer.Start();

            Timer = new System.Timers.Timer();
            Timer.Elapsed += new System.Timers.ElapsedEventHandler(OnTimer);

            new Callback<SteamGameCoordinator.MessageCallback>(OnGameCoordinatorMessage, CallbackManager);
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
            if (callback.EMsg == (uint)EGCItemMsg.k_EMsgGCUpdateItemSchema)
            {
                var msg = new ClientGCMsgProtobuf<CMsgUpdateItemSchema>(callback.Message);

                if (LastSchemaVersion != 0 && LastSchemaVersion != msg.Body.item_schema_version)
                {
                    Log.WriteInfo(string.Format("GC {0}", AppID), "Schema change from {0} to {1}", LastSchemaVersion, msg.Body.item_schema_version);

                    IRC.SendMain("{0}{1}{2} item schema updated: {3}{4}{5} -{6} {7}", Colors.OLIVE, SteamProxy.GetAppName(AppID), Colors.NORMAL, Colors.DARK_GRAY, msg.Body.item_schema_version.ToString("X4"), Colors.NORMAL, Colors.DARK_BLUE, msg.Body.items_game_url);
                }

                LastSchemaVersion = msg.Body.item_schema_version;
            }
            else if (callback.EMsg == (uint)EGCBaseClientMsg.k_EMsgGCClientWelcome)
            {
                Timer.Stop();

                var msg = new ClientGCMsgProtobuf<CMsgClientWelcome>(callback.Message);

                Log.WriteInfo(string.Format("GC {0}", AppID), "New GC session (version change from {0} to {1})", LastVersion, msg.Body.version);

                if (LastVersion != msg.Body.version)
                {
                    string message = string.Format("New {0}{1}{2} GC session {3}(version: {4} from {5})", Colors.OLIVE, SteamProxy.GetAppName(AppID), Colors.NORMAL, Colors.DARK_GRAY, msg.Body.version, LastVersion);

                    if (LastVersion != 0)
                    {
                        IRC.SendMain(message);
                    }
                    else
                    {
                        IRC.SendAnnounce(message);
                    }

                    LastVersion = msg.Body.version;
                }

                LastStatus = GCConnectionStatus.GCConnectionStatus_HAVE_SESSION;

                DbWorker.ExecuteNonQuery("INSERT INTO `GC` (`AppID`, `Status`, `Version`) VALUES(@AppID, @Status, @Version) ON DUPLICATE KEY UPDATE `Status` = @Status, `Version` = @Version",
                                         new MySqlParameter("@AppID", AppID),
                                         new MySqlParameter("@Status", GCConnectionStatus.GCConnectionStatus_HAVE_SESSION.ToString()),
                                         new MySqlParameter("@Version", msg.Body.version)
                );
            }
            else if (callback.EMsg == (uint)EGCBaseMsg.k_EMsgGCSystemMessage)
            {
                var msg = new ClientGCMsgProtobuf<CMsgSystemBroadcast>(callback.Message);

                Log.WriteInfo(string.Format("GC {0}", AppID), "Message: {0}", msg.Body.message);

                IRC.SendMain("{0}{1}{2} system message:{3} {4}", Colors.OLIVE, SteamProxy.GetAppName(AppID), Colors.NORMAL, Colors.OLIVE, msg.Body.message);
            }
            else if (callback.EMsg == (uint)EGCBaseClientMsg.k_EMsgGCClientConnectionStatus || callback.EMsg == 4008 /* tf2's k_EMsgGCClientGoodbye */)
            {
                var msg = new ClientGCMsgProtobuf<CMsgConnectionStatus>(callback.Message);

                Log.WriteInfo(string.Format("GC {0}", AppID), "Status: {0}", msg.Body.status);

                string message = string.Format("{0}{1}{2} GC status:{3} {4}", Colors.OLIVE, SteamProxy.GetAppName(AppID), Colors.NORMAL, Colors.OLIVE, msg.Body.status);

                if (LastStatus != msg.Body.status)
                {
                    IRC.SendMain(message);

                    LastStatus = msg.Body.status;
                }

                IRC.SendAnnounce(message);

                if (msg.Body.status == GCConnectionStatus.GCConnectionStatus_NO_SESSION)
                {
                    Timer.Interval = TimeSpan.FromSeconds(5).TotalMilliseconds;
                    Timer.Start();
                }

                DbWorker.ExecuteNonQuery("INSERT INTO `GC` (`AppID`, `Status`) VALUES(@AppID, @Status) ON DUPLICATE KEY UPDATE `Status` = @Status",
                                         new MySqlParameter("@AppID", AppID),
                                         new MySqlParameter("@Status", msg.Body.status.ToString())
                );
            }

            // If we hear from GC, but it's not hello, keep bugging it
            if (!Timer.Enabled && LastStatus == GCConnectionStatus.GCConnectionStatus_NO_SESSION)
            {
                Timer.Interval = TimeSpan.FromSeconds(60).TotalMilliseconds;
                Timer.Start();
            }
        }
    }
}
