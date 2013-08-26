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
using System.Linq;

namespace PICSUpdater
{
    class SteamProxy
    {
        private static SteamID steamLUG = new SteamID(103582791431044413UL);
        private static string channelSteamLUG = "#steamlug";

        private uint lastSchemaVersion = 0;

        private List<uint> importantApps = new List<uint>();
        private List<uint> importantSubs = new List<uint>();

        public SteamProxy()
        {
            new Callback<SteamFriends.ClanStateCallback>(OnClanState, Program.steam.manager);
            new Callback<SteamGameCoordinator.MessageCallback>(OnGameCoordinatorMessage, Program.steam.manager);

            ReloadImportant("");
        }

        public void ReloadImportant(string channel)
        {
            using (MySqlDataReader Reader = DbWorker.ExecuteReader(@"SELECT AppID FROM ImportantApps WHERE `Announce` = 1"))
            {
                importantApps.Clear();

                while (Reader.Read())
                {
                    importantApps.Add(Reader.GetUInt32("AppID"));
                }

                Reader.Close();
                Reader.Dispose();
            }

            using (MySqlDataReader Reader = DbWorker.ExecuteReader(@"SELECT SubID FROM ImportantSubs"))
            {
                importantSubs.Clear();

                while (Reader.Read())
                {
                    importantSubs.Add(Reader.GetUInt32("Sub"));
                }

                Reader.Close();
                Reader.Dispose();
            }

            if (!channel.Equals(""))
            {
                CommandHandler.SendEmote(channel, "reloaded {0} important apps and {1} packages", importantApps.Count, importantSubs.Count);
            }
            else
            {
                Console.WriteLine("Loaded {0} important apps and {1} packages", importantApps.Count, importantSubs.Count);
            }
        }

        private string GetPackageName(uint SubID)
        {
            String name = "";

            using (MySqlDataReader Reader = DbWorker.ExecuteReader(@"SELECT Name FROM Subs WHERE SubID = @SubID", new MySqlParameter[]
            {
                new MySqlParameter("SubID", SubID)
            }))
            {
                if (Reader.Read())
                {
                    name = DbWorker.GetString("Name", Reader);
                }

                Reader.Close();
                Reader.Dispose();
            }
            return name;
        }

        private string GetAppName(uint AppID)
        {
            String name = "";

            using (MySqlDataReader Reader = DbWorker.ExecuteReader(@"SELECT Name FROM Apps WHERE AppID = @AppID", new MySqlParameter[]
            {
                new MySqlParameter("AppID", AppID)
            }))
            {
                if (Reader.Read())
                {
                    name = DbWorker.GetString("Name", Reader);
                }

                Reader.Close();
                Reader.Dispose();
            }

            if (name.Equals("") || name.StartsWith("ValveTestApp") || name.StartsWith("SteamDB Unknown App"))
            {
                using (MySqlDataReader Reader = DbWorker.ExecuteReader(@"SELECT NewValue FROM AppsHistory WHERE AppID = @AppID AND Action = 'created_info' AND `Key` = 1 LIMIT 1", new MySqlParameter[]
                {
                    new MySqlParameter("AppID", AppID)
                }))
                {
                    if (Reader.Read())
                    {
                        string nameOld = DbWorker.GetString("NewValue", Reader);

                        if (!name.Equals(nameOld))
                        {
                            name = string.Format ("{0} {1}({2}){3}", name, Colors.DARK_GRAY, nameOld, Colors.NORMAL);
                        }
                    }

                    Reader.Close();
                    Reader.Dispose();
                }
            }

            return name;
        }

        private void OnClanState(SteamFriends.ClanStateCallback callback)
        {
            string ClanName = callback.ClanName;
            string Message = "";

            if (ClanName == null)
            {
                ClanName = Program.steam.steamFriends.GetClanName(callback.ClanID);
            }

            if (ClanName == "")
            {
                ClanName = "Group";
            }
            else
            {
                ClanName = string.Format("{0}{1}{2}", Colors.OLIVE, ClanName, Colors.NORMAL);
            }

            foreach (var announcement in callback.Announcements)
            {
                Message = string.Format("{0} announcement: {1}{2}{3} -{4} http://steamcommunity.com/gid/{5}/announcements/detail/{6}", ClanName, Colors.GREEN, announcement.Headline.ToString(), Colors.NORMAL, Colors.DARK_BLUE, callback.ClanID, announcement.ID);

                CommandHandler.Send(Program.channelMain, Message);

                // Additionally send announcements to steamlug channel
                if (callback.ClanID == steamLUG)
                {
                    CommandHandler.Send(channelSteamLUG, Message);
                }
            }

            foreach(var groupevent in callback.Events)
            {
                if (groupevent.JustPosted == true)
                {
                    Message = string.Format("{0} event: {1}{2}{3} -{4} http://steamcommunity.com/gid/{5}/events/{6}", ClanName, Colors.GREEN, groupevent.Headline.ToString(), Colors.NORMAL, Colors.DARK_BLUE, callback.ClanID, groupevent.ID);

                    // Send events only to steamlug channel
                    if (callback.ClanID == steamLUG)
                    {
                        CommandHandler.Send(channelSteamLUG, Message);
                    }
                    else
                    {
                        CommandHandler.Send(Program.channelMain, Message);
                    }
                }
            }
        }

        public void OnPICSChanges(uint changeNumber, Dictionary<uint, uint> appList, Dictionary<uint, uint> packageList)
        {
            string Message = string.Format("Received changelist {0}{1}{2} with {3}{4}{5} apps and {6}{7}{8} packages -{9} http://steamdb.info/changelist/{10}/",
                                           Colors.OLIVE, changeNumber, Colors.NORMAL,
                                           appList.Count >= 10 ? Colors.YELLOW : Colors.OLIVE, appList.Count, Colors.NORMAL,
                                           packageList.Count >= 10 ? Colors.YELLOW : Colors.OLIVE, packageList.Count, Colors.NORMAL,
                                           Colors.DARK_BLUE, changeNumber);

            CommandHandler.Send(Program.channelAnnounce, "{0}»{1} {2}",  Colors.RED, Colors.NORMAL, Message);

            if(appList.Count >= 50 || packageList.Count >= 50)
            {
                CommandHandler.Send(Program.channelMain, Message);
            }

            ProcessAppChanges(changeNumber, appList);
            ProcessSubChanges(changeNumber, packageList);

            //ProcessAppChanges(changeNumber, appList.OrderBy(key => key.Value).ToDictionary(pair => pair.Key, pair => pair.Value));
            //ProcessSubChanges(packageList.OrderBy(key => key.Value).ToDictionary(pair => pair.Key, pair => pair.Value));
        }

        private void ProcessAppChanges(uint changeNumber, Dictionary<uint, uint> appList)
        {
            string name = "";
            bool isImportant = false;

            foreach (var app in appList)
            {
                name = GetAppName(app.Key);
                isImportant = importantApps.Contains(app.Key);

                /*if (changeNumber != app.Value)
                {
                    changeNumber = app.Value;

                    CommandHandler.Send(Program.channelAnnounce, "{0}»{1} Bundled changelist {2}{3}{4} -{5} http://steamdb.info/changelist/{6}/",  Colors.BLUE, Colors.LIGHT_GRAY, Colors.OLIVE, changeNumber, Colors.LIGHT_GRAY, Colors.DARK_BLUE, changeNumber);
                }*/

                if (isImportant)
                {
                    CommandHandler.Send(Program.channelMain, "Important app update: {0}{1}{2} -{3} http://steamdb.info/app/{4}/#section_history", Colors.OLIVE, name, Colors.NORMAL, Colors.DARK_BLUE, app.Key);
                }

                if (name.Equals(""))
                {
                    //CommandHandler.Send(Program.channelAnnounce, "  App: {0}{1}{2}", Colors.GREEN, app.Key, Colors.NORMAL);
                    name = string.Format("{0}{1}{2}", Colors.GREEN, app.Key, Colors.NORMAL);
                }
                else
                {
                    //CommandHandler.Send(Program.channelAnnounce, "  App: {0}{1}{2} - {3}", isImportant ? Colors.OLIVE : Colors.LIGHT_GRAY, app.Key, Colors.NORMAL, name);
                    name = string.Format("{0}{1}{2} - {3}", isImportant ? Colors.YELLOW : Colors.LIGHT_GRAY, app.Key, Colors.NORMAL, name);
                }

                if (changeNumber != app.Value)
                {
                    CommandHandler.Send(Program.channelAnnounce, "  App: {0} - bundled changelist {1}{2}{3} -{4} http://steamdb.info/changelist/{5}/", name, Colors.OLIVE, app.Value, Colors.NORMAL, Colors.DARK_BLUE, app.Value);
                }
                else
                {
                    CommandHandler.Send(Program.channelAnnounce, "  App: {0}", name);
                }
            }
        }

        private void ProcessSubChanges(uint changeNumber, Dictionary<uint, uint> packageList)
        {
            string name = "";
            bool isImportant = false;

            foreach (var package in packageList)
            {
                name = GetPackageName(package.Key);
                isImportant = importantSubs.Contains(package.Key);

                if (isImportant)
                {
                    CommandHandler.Send(Program.channelMain, "Important package update: {0}{1}{2} -{3} http://steamdb.info/sub/{4}/#section_history", Colors.OLIVE, name, Colors.NORMAL, Colors.DARK_BLUE, package.Value);
                }

                if (name.Equals(""))
                {
                    name = string.Format("{0}{1}{2}", Colors.GREEN, package.Key, Colors.NORMAL);
                }
                else
                {
                    name = string.Format("{0}{1}{2} - {3}", isImportant ? Colors.YELLOW : Colors.LIGHT_GRAY, package.Key, Colors.NORMAL, name);
                }

                if (changeNumber != package.Value)
                {
                    CommandHandler.Send(Program.channelAnnounce, "  Package: {0} - bundled changelist {1}{2}{3} -{4} http://steamdb.info/changelist/{5}/", name, Colors.OLIVE, package.Value, Colors.NORMAL, Colors.DARK_BLUE, package.Value);
                }
                else
                {
                    CommandHandler.Send(Program.channelAnnounce, "  Package: {0}", name);
                }
            }
        }

        public void PlayGame(uint AppID)
        {
            var clientMsg = new ClientMsgProtobuf<CMsgClientGamesPlayed>( EMsg.ClientGamesPlayedNoDataBlob );

            clientMsg.Body.games_played.Add( new CMsgClientGamesPlayed.GamePlayed
                                            {
                game_id = AppID
            } );

            Program.steam.steamClient.Send( clientMsg );
        }

        private void OnGameCoordinatorMessage(SteamGameCoordinator.MessageCallback callback)
        {
            if (callback.EMsg == (uint)EGCItemMsg.k_EMsgGCUpdateItemSchema)
            {
                var msg = new ClientGCMsgProtobuf<CMsgUpdateItemSchema>(callback.Message);

                if (lastSchemaVersion != 0 && lastSchemaVersion != msg.Body.item_schema_version)
                {
                    CommandHandler.Send(Program.channelMain, "New TF2 item schema {0}(version {1}){2} -{3} {4}", Colors.DARK_GRAY, msg.Body.item_schema_version.ToString("X4"), Colors.NORMAL, Colors.DARK_BLUE, msg.Body.items_game_url);
                }

                lastSchemaVersion = msg.Body.item_schema_version;
            }
            else if (callback.EMsg == (uint)EGCBaseMsg.k_EMsgGCSystemMessage)
            {
                var msg = new ClientGCMsgProtobuf<CMsgSystemBroadcast>(callback.Message);

                CommandHandler.Send(Program.channelMain, "GC system message:{0} {1}", Colors.OLIVE, msg.Body.message);
            }
            else if (callback.EMsg == (uint)EGCBaseClientMsg.k_EMsgGCClientWelcome)
            {
                var msg = new ClientGCMsgProtobuf<CMsgClientWelcome>(callback.Message);

                CommandHandler.Send(Program.channelAnnounce, "New GC session {0}(version: {1})", Colors.DARK_GRAY, msg.Body.version);
            }
            else if (callback.EMsg == (uint)EGCBaseClientMsg.k_EMsgGCClientConnectionStatus || callback.EMsg == 4008 /* tf2's k_EMsgGCClientGoodbye */)
            {
                var msg = new ClientGCMsgProtobuf<CMsgConnectionStatus>(callback.Message);

                CommandHandler.Send(Program.channelAnnounce, "GC status:{0} {1}", Colors.OLIVE, msg.Body.status);
            }
        }
    }
}
