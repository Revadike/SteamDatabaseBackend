/*
 * Copyright (c) 2013, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections.Generic;
using System.IO;
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
        public enum IRCRequestType { TYPE_APP, TYPE_SUB, TYPE_PLAYERS };

        public class IRCRequest
        {
            public JobID JobID { get; set; }

            public string Channel { get; set; }
            public string Requester { get; set; }

            public IRCRequestType Type { get; set; }

            public uint Target { get; set; }
        }

        public class GCInfo
        {
            public uint AppID;
            public uint LastVersion;
            public uint LastSchemaVersion;
            public GCConnectionStatus LastStatus;
        }

        public List<IRCRequest> IRCRequests { get; private set; }
        public static List<GCInfo> GCInfos = new List<GCInfo>();

        private static SteamID steamLUG = new SteamID(103582791431044413UL);
        private static string channelSteamLUG = "#steamlug";

        private List<uint> importantApps;
        private List<uint> importantSubs;

        public SteamProxy()
        {
            IRCRequests = new List<IRCRequest>();
            importantApps = new List<uint>();
            importantSubs = new List<uint>();

            ReloadImportant("");
        }

        public void ReloadImportant(string channel)
        {
            using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT AppID FROM ImportantApps WHERE `Announce` = 1"))
            {
                importantApps.Clear();

                while (Reader.Read())
                {
                    importantApps.Add(Reader.GetUInt32("AppID"));
                }
            }

            using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT SubID FROM ImportantSubs"))
            {
                importantSubs.Clear();

                while (Reader.Read())
                {
                    importantSubs.Add(Reader.GetUInt32("SubID"));
                }
            }

            if (!channel.Equals(""))
            {
                CommandHandler.SendEmote(channel, "reloaded {0} important apps and {1} packages", importantApps.Count, importantSubs.Count);
            }
            else
            {
                Log.WriteInfo("IRC Proxy", "Loaded {0} important apps and {1} packages", importantApps.Count, importantSubs.Count);
            }
        }

        private static string GetPackageName(uint SubID)
        {
            String name = "";

            using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT Name FROM Subs WHERE SubID = @SubID", new MySqlParameter[]
            {
                new MySqlParameter("SubID", SubID)
            }))
            {
                if (Reader.Read())
                {
                    name = DbWorker.GetString("Name", Reader);
                }
            }
            return name;
        }

        private static string GetAppName(uint AppID)
        {
            String name = "";

            using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT Name FROM Apps WHERE AppID = @AppID", new MySqlParameter[]
            {
                new MySqlParameter("AppID", AppID)
            }))
            {
                if (Reader.Read())
                {
                    name = DbWorker.GetString("Name", Reader);
                }
            }

            if (name.Equals("") || name.StartsWith("ValveTestApp") || name.StartsWith("SteamDB Unknown App"))
            {
                using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT NewValue FROM AppsHistory WHERE AppID = @AppID AND Action = 'created_info' AND `Key` = 1 LIMIT 1", new MySqlParameter[]
                {
                    new MySqlParameter("AppID", AppID)
                }))
                {
                    if (Reader.Read())
                    {
                        string nameOld = DbWorker.GetString("NewValue", Reader);

                        if (name.Equals(""))
                        {
                            name = string.Format("AppID {0}", AppID);
                        }

                        if (!name.Equals(nameOld))
                        {
                            name = string.Format ("{0} {1}({2}){3}", name, Colors.DARK_GRAY, nameOld, Colors.NORMAL);
                        }
                    }
                }
            }

            return name;
        }

        public void OnClanState(SteamFriends.ClanStateCallback callback)
        {
            if (callback.Events.Count == 0 && callback.Announcements.Count == 0)
            {
                return;
            }

            string ClanName = callback.ClanName;
            string Message = "";

            if (ClanName == null)
            {
                ClanName = Program.steam.steamFriends.GetClanName(callback.ClanID);
            }

            if (ClanName == "")
            {
                ClanName = "Group";

                Log.WriteError("IRC Proxy", "ClanID: {0} - no group name", callback.ClanID);
            }

            foreach (var announcement in callback.Announcements)
            {
                Message = string.Format("{0}{1}{2} announcement: {3}{4}{5} -{6} http://steamcommunity.com/gid/{7}/announcements/detail/{8}", Colors.OLIVE, ClanName, Colors.NORMAL, Colors.GREEN, announcement.Headline.ToString(), Colors.NORMAL, Colors.DARK_BLUE, callback.ClanID, announcement.ID);

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
                    Message = string.Format("{0}{1}{2} event: {3}{4}{5} -{6} http://steamcommunity.com/gid/{7}/events/{8}", Colors.OLIVE, ClanName, Colors.NORMAL, Colors.GREEN, groupevent.Headline.ToString(), Colors.NORMAL, Colors.DARK_BLUE, callback.ClanID, groupevent.ID);

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

        public void OnNumberOfPlayers(SteamUserStats.NumberOfPlayersCallback callback, JobID jobID)
        {
            var request = IRCRequests.Find(r => r.JobID == jobID);

            if (request == null)
            {
                return;
            }

            IRCRequests.Remove(request);

            Log.WriteInfo("IRC Proxy", "Numplayers request completed for {0} in {1}", request.Requester, request.Channel);

            if (callback.Result != EResult.OK)
            {
                CommandHandler.Send(request.Channel, "{0}{1}{2}: Unable to request player count: {4}", Colors.OLIVE, request.Requester, Colors.NORMAL, callback.Result);
            }
            else
            {
                string name = GetAppName(request.Target);

                if (name.Equals(""))
                {
                    name = string.Format("AppID {0}", request.Target);
                }

                CommandHandler.Send(request.Channel, "{0}{1}{2}: People playing {3}{4}{5} right now: {6}{7}", Colors.OLIVE, request.Requester, Colors.NORMAL, Colors.OLIVE, name, Colors.NORMAL, Colors.YELLOW, callback.NumPlayers.ToString("N0"));
            }
        }

        public void OnProductInfo(IRCRequest request, SteamApps.PICSProductInfoCallback callback)
        {
            Log.WriteInfo("IRC Proxy", "Product info request completed for {0} in {1}", request.Requester, request.Channel);

            if (request.Type == SteamProxy.IRCRequestType.TYPE_SUB)
            {
                if (!callback.Packages.ContainsKey(request.Target))
                {
                    CommandHandler.Send(request.Channel, "{0}{1}{2}: Unknown SubID: {3}{4}", Colors.OLIVE, request.Requester, Colors.NORMAL, Colors.OLIVE, request.Target);

                    return;
                }

                var info = callback.Packages[request.Target];
                var kv = info.KeyValues.Children.FirstOrDefault(); // Blame VoiDeD
                string name = string.Format("AppID {0}", info.ID);

                if (kv["name"].Value != null)
                {
                    name = kv["name"].AsString();
                }

                try
                {
                    kv.SaveToFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sub", string.Format("{0}.vdf", info.ID)), false);
                }
                catch (Exception e)
                {
                    CommandHandler.Send(request.Channel, "{0}{1}{2}: Unable to save file for {3}: {4}", Colors.OLIVE, request.Requester, Colors.NORMAL, name, e.Message);

                    return;
                }

                CommandHandler.Send(request.Channel, "{0}{1}{2}: Dump for {3}{4}{5} -{6} http://raw.steamdb.info/sub/{7}.vdf{8}{9}", Colors.OLIVE, request.Requester, Colors.NORMAL, Colors.OLIVE, name, Colors.NORMAL, Colors.DARK_BLUE, info.ID, Colors.NORMAL, info.MissingToken ? " (mising token)" : "");
            }
            else if (request.Type == SteamProxy.IRCRequestType.TYPE_APP)
            {
                if (!callback.Apps.ContainsKey(request.Target))
                {
                    CommandHandler.Send(request.Channel, "{0}{1}{2}: Unknown AppID: {3}{4}", Colors.OLIVE, request.Requester, Colors.NORMAL, Colors.OLIVE, request.Target);

                    return;
                }

                var info = callback.Apps[request.Target];
                string name = string.Format("AppID {0}", info.ID);

                if (info.KeyValues["common"]["name"].Value != null)
                {
                    name = info.KeyValues["common"]["name"].AsString();
                }

                try
                {
                    info.KeyValues.SaveToFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app", string.Format("{0}.vdf", info.ID)), false);
                }
                catch (Exception e)
                {
                    CommandHandler.Send(request.Channel, "{0}{1}{2}: Unable to save file for {3}: {4}", Colors.OLIVE, request.Requester, Colors.NORMAL, name, e.Message);

                    return;
                }

                CommandHandler.Send(request.Channel, "{0}{1}{2}: Dump for {3}{4}{5} -{6} http://raw.steamdb.info/app/{7}.vdf{8}{9}", Colors.OLIVE, request.Requester, Colors.NORMAL, Colors.OLIVE, name, Colors.NORMAL, Colors.DARK_BLUE, info.ID, Colors.NORMAL, info.MissingToken ? " (mising token)" : "");
            }
            else
            {
                CommandHandler.Send(request.Channel, "{0}{1}{2}: I have no idea what happened here!", Colors.OLIVE, request.Requester, Colors.NORMAL);
            }
        }

        public void OnPICSChanges(uint changeNumber, SteamApps.PICSChangesCallback callback)
        {
            string Message = string.Format("Received changelist {0}{1}{2} with {3}{4}{5} apps and {6}{7}{8} packages -{9} http://steamdb.info/changelist/{10}/",
                                           Colors.OLIVE, changeNumber, Colors.NORMAL,
                                           callback.AppChanges.Count >= 10 ? Colors.YELLOW : Colors.OLIVE, callback.AppChanges.Count, Colors.NORMAL,
                                           callback.PackageChanges.Count >= 10 ? Colors.YELLOW : Colors.OLIVE, callback.PackageChanges.Count, Colors.NORMAL,
                                           Colors.DARK_BLUE, changeNumber);

            CommandHandler.Send(Program.channelAnnounce, "{0}»{1} {2}",  Colors.RED, Colors.NORMAL, Message);

            if(callback.AppChanges.Count >= 50 || callback.PackageChanges.Count >= 50)
            {
                CommandHandler.Send(Program.channelMain, Message);
            }

            if (callback.AppChanges.Count > 0)
            {
                ProcessAppChanges(changeNumber, callback.AppChanges);
            }

            if (callback.PackageChanges.Count > 0)
            {
                ProcessSubChanges(changeNumber, callback.PackageChanges);
            }
        }

        private void ProcessAppChanges(uint changeNumber, Dictionary<uint, SteamApps.PICSChangesCallback.PICSChangeData> appList)
        {
            string name = "";
            bool isImportant = false;

            foreach (var app in appList)
            {
                name = GetAppName(app.Value.ID);
                isImportant = importantApps.Contains(app.Value.ID);

                /*if (changeNumber != app.Value.ChangeNumber)
                {
                    changeNumber = app.Value;

                    CommandHandler.Send(Program.channelAnnounce, "{0}»{1} Bundled changelist {2}{3}{4} -{5} http://steamdb.info/changelist/{6}/",  Colors.BLUE, Colors.LIGHT_GRAY, Colors.OLIVE, changeNumber, Colors.LIGHT_GRAY, Colors.DARK_BLUE, changeNumber);
                }*/

                if (isImportant)
                {
                    CommandHandler.Send(Program.channelMain, "Important app update: {0}{1}{2} -{3} http://steamdb.info/app/{4}/#section_history", Colors.OLIVE, name, Colors.NORMAL, Colors.DARK_BLUE, app.Value.ID);
                }

                if (name.Equals(""))
                {
                    name = string.Format("{0}{1}{2}", Colors.GREEN, app.Value.ID, Colors.NORMAL);
                }
                else
                {
                    name = string.Format("{0}{1}{2} - {3}", isImportant ? Colors.YELLOW : Colors.LIGHT_GRAY, app.Value.ID, Colors.NORMAL, name);
                }

                if (changeNumber != app.Value.ChangeNumber)
                {
                    CommandHandler.Send(Program.channelAnnounce, "  App: {0} - bundled changelist {1}{2}{3} -{4} http://steamdb.info/changelist/{5}/", name, Colors.OLIVE, app.Value.ChangeNumber, Colors.NORMAL, Colors.DARK_BLUE, app.Value.ChangeNumber);
                }
                else
                {
                    CommandHandler.Send(Program.channelAnnounce, "  App: {0}{1}", name, app.Value.NeedsToken ? " (requires token)" : "");
                }
            }
        }

        private void ProcessSubChanges(uint changeNumber, Dictionary<uint, SteamApps.PICSChangesCallback.PICSChangeData> packageList)
        {
            string name = "";
            bool isImportant = false;

            foreach (var package in packageList)
            {
                name = GetPackageName(package.Value.ID);
                isImportant = importantSubs.Contains(package.Value.ID);

                if (isImportant)
                {
                    CommandHandler.Send(Program.channelMain, "Important package update: {0}{1}{2} -{3} http://steamdb.info/sub/{4}/#section_history", Colors.OLIVE, name, Colors.NORMAL, Colors.DARK_BLUE, package.Value.ID);
                }

                if (name.Equals(""))
                {
                    name = string.Format("{0}{1}{2}", Colors.GREEN, package.Value.ID, Colors.NORMAL);
                }
                else
                {
                    name = string.Format("{0}{1}{2} - {3}", isImportant ? Colors.YELLOW : Colors.LIGHT_GRAY, package.Value.ID, Colors.NORMAL, name);
                }

                if (changeNumber != package.Value.ChangeNumber)
                {
                    CommandHandler.Send(Program.channelAnnounce, "  Package: {0} - bundled changelist {1}{2}{3} -{4} http://steamdb.info/changelist/{5}/", name, Colors.OLIVE, package.Value.ChangeNumber, Colors.NORMAL, Colors.DARK_BLUE, package.Value.ChangeNumber);
                }
                else
                {
                    CommandHandler.Send(Program.channelAnnounce, "  Package: {0}{1}", name, package.Value.NeedsToken ? " (requires token)" : "");
                }
            }
        }

        public static void PlayGame(SteamClient client, uint AppID)
        {
            var clientMsg = new ClientMsgProtobuf<CMsgClientGamesPlayed>( EMsg.ClientGamesPlayed );

            clientMsg.Body.games_played.Add( new CMsgClientGamesPlayed.GamePlayed
            {
                game_id = AppID
            } );

            client.Send(clientMsg);
        }

        public static void GameCoordinatorMessage(uint AppID, SteamGameCoordinator.MessageCallback callback, SteamGameCoordinator gc)
        {
            GCInfo info = GCInfos.Find(r => r.AppID == AppID);

            if (info == null)
            {
                info = new GCInfo
                {
                    AppID = AppID,
                    LastVersion = 0,
                    LastSchemaVersion = 0,
                    LastStatus = GCConnectionStatus.GCConnectionStatus_NO_STEAM
                };

                GCInfos.Add(info);
            }

            if (callback.EMsg == (uint)EGCItemMsg.k_EMsgGCUpdateItemSchema)
            {
                var msg = new ClientGCMsgProtobuf<CMsgUpdateItemSchema>(callback.Message);

                if (info.LastSchemaVersion != 0 && info.LastSchemaVersion != msg.Body.item_schema_version)
                {
                    Log.WriteInfo(string.Format("GC {0}", AppID), "Schema change from {0} to {1}", info.LastSchemaVersion, msg.Body.item_schema_version);

                    CommandHandler.Send(Program.channelMain, "{0}{1}{2} item schema updated: {3}{4}{5} -{6} {7}", Colors.OLIVE, GetAppName(AppID), Colors.NORMAL, Colors.DARK_GRAY, msg.Body.item_schema_version.ToString("X4"), Colors.NORMAL, Colors.DARK_BLUE, msg.Body.items_game_url);
                }

                info.LastSchemaVersion = msg.Body.item_schema_version;
            }
            else if (callback.EMsg == (uint)EGCBaseClientMsg.k_EMsgGCClientWelcome)
            {
                var msg = new ClientGCMsgProtobuf<CMsgClientWelcome>(callback.Message);

                if (info.LastVersion != msg.Body.version)
                {
                    Log.WriteInfo(string.Format("GC {0}", AppID), "Version change from {0} to {1}", info.LastVersion, msg.Body.version);

                    string message = string.Format("New {0}{1}{2} GC session {3}(version: {4})", Colors.OLIVE, GetAppName(AppID), Colors.NORMAL, Colors.DARK_GRAY, msg.Body.version);

                    if(info.LastVersion != 0)
                    {
                        CommandHandler.Send(Program.channelMain, message);
                    }

                    CommandHandler.Send(Program.channelAnnounce, message);

                    info.LastVersion = msg.Body.version;
                }
            }
            else if (callback.EMsg == (uint)EGCBaseMsg.k_EMsgGCSystemMessage)
            {
                var msg = new ClientGCMsgProtobuf<CMsgSystemBroadcast>(callback.Message);

                Log.WriteInfo(string.Format("GC {0}", AppID), "Message: {0}", msg.Body.message);

                CommandHandler.Send(Program.channelMain, "{0}{1}{2} system message:{3} {4}", Colors.OLIVE, GetAppName(AppID), Colors.NORMAL, Colors.OLIVE, msg.Body.message);
            }
            else if (callback.EMsg == (uint)EGCBaseClientMsg.k_EMsgGCClientConnectionStatus || callback.EMsg == 4008 /* tf2's k_EMsgGCClientGoodbye */)
            {
                var msg = new ClientGCMsgProtobuf<CMsgConnectionStatus>(callback.Message);

                Log.WriteInfo(string.Format("GC {0}", AppID), "Status: {0}", msg.Body.status);

                string message = string.Format("{0}{1}{2} GC status:{3} {4}", Colors.OLIVE, GetAppName(AppID), Colors.NORMAL, Colors.OLIVE, msg.Body.status);

                if (info.LastStatus != msg.Body.status)
                {
                    CommandHandler.Send(Program.channelMain, message);

                    info.LastStatus = msg.Body.status;
                }

                CommandHandler.Send(Program.channelAnnounce, message);

                if (msg.Body.status == GCConnectionStatus.GCConnectionStatus_NO_SESSION)
                {
                    GameCoordinatorHello(AppID, gc);
                }
            }
        }

        public static void GameCoordinatorHello(uint AppID, SteamGameCoordinator gc)
        {
            var clientHello = new ClientGCMsgProtobuf<CMsgClientHello>( ( uint )EGCBaseClientMsg.k_EMsgGCClientHello );

            gc.Send(clientHello, AppID);
        }
    }
}
