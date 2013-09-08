/*
 * Copyright (c) 2013, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MySql.Data.MySqlClient;
using SteamKit2;
using SteamKit2.GC;
using SteamKit2.GC.Internal;
using SteamKit2.Internal;

namespace SteamDatabaseBackend
{
    public class SteamProxy
    {
        public enum IRCRequestType
        {
            TYPE_APP,
            TYPE_SUB,
            TYPE_PLAYERS
        }

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

        public List<IRCRequest> IRCRequests = new List<IRCRequest>();

        public static List<GCInfo> GCInfos = new List<GCInfo>();

        private static SteamID steamLUG = new SteamID(103582791431044413UL);
        private static string channelSteamLUG = "#steamlug";

        private List<uint> importantApps = new List<uint>();
        private List<uint> importantSubs = new List<uint>();

        public void Run()
        {
            System.Timers.Timer timer = new System.Timers.Timer();
            timer.Elapsed += new System.Timers.ElapsedEventHandler(OnTimer);
            timer.Interval = TimeSpan.FromMinutes(5).TotalMilliseconds;
            timer.Start();

            ReloadImportant();
        }

        private void OnTimer(object sender, System.Timers.ElapsedEventArgs e)
        {
            PlayGame(Program.steam.steamClient, Steam.TEAM_FORTRESS_2);

            if (Program.steamDota.isRunning)
            {
                PlayGame(Program.steamDota.steamClient, SteamDota.DOTA_2);
            }
        }

        public void ReloadImportant(string channel = "")
        {
            using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `AppID` FROM `ImportantApps` WHERE `Announce` = 1"))
            {
                importantApps.Clear();

                while (Reader.Read())
                {
                    importantApps.Add(Reader.GetUInt32("AppID"));
                }
            }

            using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `SubID` FROM `ImportantSubs`"))
            {
                importantSubs.Clear();

                while (Reader.Read())
                {
                    importantSubs.Add(Reader.GetUInt32("SubID"));
                }
            }

            if (!channel.Equals(string.Empty))
            {
                IRC.Send(channel, "reloaded {0} important apps and {1} packages", importantApps.Count, importantSubs.Count);
            }
            else
            {
                Log.WriteInfo("IRC Proxy", "Loaded {0} important apps and {1} packages", importantApps.Count, importantSubs.Count);
            }
        }

        private static string GetPackageName(uint SubID)
        {
            string name = string.Empty;

            using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `Name`, `StoreName` FROM `Subs` WHERE `SubID` = @SubID", new MySqlParameter("SubID", SubID)))
            {
                if (Reader.Read())
                {
                    name = DbWorker.GetString("Name", Reader);

                    if (name.StartsWith("Steam Sub", StringComparison.Ordinal))
                    {
                        string nameStore = DbWorker.GetString("NameStore", Reader);

                        if (!string.IsNullOrEmpty(nameStore))
                        {
                            name = string.Format("{0} {1}({2}){3}", name, Colors.DARK_GRAY, nameStore, Colors.NORMAL);
                        }
                    }
                }
            }

            return name;
        }

        private static string GetAppName(uint AppID)
        {
            string name = string.Empty;
            string nameStore = string.Empty;

            using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `Name`, `StoreName` FROM `Apps` WHERE `AppID` = @AppID", new MySqlParameter("AppID", AppID)))
            {
                if (Reader.Read())
                {
                    name = DbWorker.GetString("Name", Reader);
                    nameStore = DbWorker.GetString("StoreName", Reader);
                }
            }

            if (name.Equals(string.Empty) || name.StartsWith("ValveTestApp", StringComparison.Ordinal) || name.StartsWith("SteamDB Unknown App", StringComparison.Ordinal))
            {
                if (!string.IsNullOrEmpty(nameStore))
                {
                    return string.Format("{0} {1}({2}){3}", name, Colors.DARK_GRAY, nameStore, Colors.NORMAL);
                }

                using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `NewValue` FROM `AppsHistory` WHERE `AppID` = @AppID AND `Action` = 'created_info' AND `Key` = 1 LIMIT 1", new MySqlParameter("AppID", AppID)))
                {
                    if (Reader.Read())
                    {
                        nameStore = DbWorker.GetString("NewValue", Reader);

                        if (name.Equals(string.Empty))
                        {
                            name = string.Format("AppID {0}", AppID);
                        }

                        if (!name.Equals(nameStore))
                        {
                            name = string.Format("{0} {1}({2}){3}", name, Colors.DARK_GRAY, nameStore, Colors.NORMAL);
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
            string Message = string.Empty;

            if (ClanName.Equals(null))
            {
                ClanName = Program.steam.steamFriends.GetClanName(callback.ClanID);
            }

            if (ClanName.Equals(string.Empty))
            {
                ClanName = "Group";

                Log.WriteError("IRC Proxy", "ClanID: {0} - no group name", callback.ClanID);
            }

            foreach (var announcement in callback.Announcements)
            {
                Message = string.Format("{0}{1}{2} announcement: {3}{4}{5} -{6} http://steamcommunity.com/gid/{7}/announcements/detail/{8}", Colors.OLIVE, ClanName, Colors.NORMAL, Colors.GREEN, announcement.Headline, Colors.NORMAL, Colors.DARK_BLUE, callback.ClanID, announcement.ID);

                IRC.SendMain(Message);

                // Additionally send announcements to steamlug channel
                if (callback.ClanID.Equals(steamLUG))
                {
                    IRC.Send(channelSteamLUG, Message);
                }
            }

            foreach (var groupevent in callback.Events)
            {
                if (groupevent.JustPosted)
                {
                    Message = string.Format("{0}{1}{2} event: {3}{4}{5} -{6} http://steamcommunity.com/gid/{7}/events/{8}", Colors.OLIVE, ClanName, Colors.NORMAL, Colors.GREEN, groupevent.Headline, Colors.NORMAL, Colors.DARK_BLUE, callback.ClanID, groupevent.ID);

                    // Send events only to steamlug channel
                    if (callback.ClanID.Equals(steamLUG))
                    {
                        IRC.Send(channelSteamLUG, Message);
                    }
                    else
                    {
                        IRC.SendMain(Message);
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
                IRC.Send(request.Channel, "{0}{1}{2}: Unable to request player count: {3}", Colors.OLIVE, request.Requester, Colors.NORMAL, callback.Result);
            }
            else
            {
                string name;
                string graph = string.Empty;

                if (request.Target == 0)
                {
                    name = "Steam";
                }
                else
                {
                    name = GetAppName(request.Target);

                    if (name.Equals(string.Empty))
                    {
                        name = string.Format("AppID {0}", request.Target);
                    }
                }

                using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `AppID` FROM `ImportantApps` WHERE `Graph` = 1"))
                {
                    if (Reader.Read())
                    {
                        graph = string.Format("{0} - graph:{1} http://steamdb.info/graph/{2}/", Colors.NORMAL, Colors.DARK_BLUE, request.Target);
                    }
                }

                IRC.Send(request.Channel, "{0}{1}{2}: People playing {3}{4}{5} right now: {6}{7}{8}", Colors.OLIVE, request.Requester, Colors.NORMAL, Colors.OLIVE, name, Colors.NORMAL, Colors.YELLOW, callback.NumPlayers.ToString("N0"), graph);
            }
        }

        public void OnProductInfo(IRCRequest request, SteamApps.PICSProductInfoCallback callback)
        {
            Log.WriteInfo("IRC Proxy", "Product info request completed for {0} in {1}", request.Requester, request.Channel);

            if (request.Type == SteamProxy.IRCRequestType.TYPE_SUB)
            {
                if (!callback.Packages.ContainsKey(request.Target))
                {
                    IRC.Send(request.Channel, "{0}{1}{2}: Unknown SubID: {3}{4}", Colors.OLIVE, request.Requester, Colors.NORMAL, Colors.OLIVE, request.Target);

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
                    IRC.Send(request.Channel, "{0}{1}{2}: Unable to save file for {3}: {4}", Colors.OLIVE, request.Requester, Colors.NORMAL, name, e.Message);

                    return;
                }

                IRC.Send(request.Channel, "{0}{1}{2}: Dump for {3}{4}{5} -{6} http://raw.steamdb.info/sub/{7}.vdf{8}{9}",
                                    Colors.OLIVE, request.Requester, Colors.NORMAL,
                                    Colors.OLIVE, name, Colors.NORMAL,
                                    Colors.DARK_BLUE, info.ID, Colors.NORMAL,
                                    info.MissingToken ? " (mising token)" : string.Empty
                );
            }
            else if (request.Type == SteamProxy.IRCRequestType.TYPE_APP)
            {
                if (!callback.Apps.ContainsKey(request.Target))
                {
                    IRC.Send(request.Channel, "{0}{1}{2}: Unknown AppID: {3}{4}", Colors.OLIVE, request.Requester, Colors.NORMAL, Colors.OLIVE, request.Target);

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
                    IRC.Send(request.Channel, "{0}{1}{2}: Unable to save file for {3}: {4}", Colors.OLIVE, request.Requester, Colors.NORMAL, name, e.Message);

                    return;
                }

                IRC.Send(request.Channel, "{0}{1}{2}: Dump for {3}{4}{5} -{6} http://raw.steamdb.info/app/{7}.vdf{8}{9}",
                                    Colors.OLIVE, request.Requester, Colors.NORMAL,
                                    Colors.OLIVE, name, Colors.NORMAL,
                                    Colors.DARK_BLUE, info.ID, Colors.NORMAL,
                                    info.MissingToken ? " (mising token)" : string.Empty
                );
            }
            else
            {
                IRC.Send(request.Channel, "{0}{1}{2}: I have no idea what happened here!", Colors.OLIVE, request.Requester, Colors.NORMAL);
            }
        }

        public void OnPICSChanges(uint changeNumber, SteamApps.PICSChangesCallback callback)
        {
            string Message = string.Format("Received changelist {0}{1}{2} with {3}{4}{5} apps and {6}{7}{8} packages -{9} http://steamdb.info/changelist/{10}/",
                                           Colors.OLIVE, changeNumber, Colors.NORMAL,
                                           callback.AppChanges.Count >= 10 ? Colors.YELLOW : Colors.OLIVE, callback.AppChanges.Count, Colors.NORMAL,
                                           callback.PackageChanges.Count >= 10 ? Colors.YELLOW : Colors.OLIVE, callback.PackageChanges.Count, Colors.NORMAL,
                                           Colors.DARK_BLUE, changeNumber
                             );

            if (callback.AppChanges.Count >= 50 || callback.PackageChanges.Count >= 50)
            {
                IRC.SendMain(Message);
            }

            IRC.SendAnnounce("{0}»{1} {2}",  Colors.RED, Colors.NORMAL, Message);

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
            string name;

            var important = appList.Keys.Intersect(importantApps);

            foreach (var app in important)
            {
                IRC.SendMain("Important app update: {0}{1}{2} -{3} http://steamdb.info/app/{4}/#section_history", Colors.OLIVE, GetAppName(app), Colors.NORMAL, Colors.DARK_BLUE, app);
            }

            foreach (var app in appList.Values)
            {
                name = GetAppName(app.ID);

                /*if (changeNumber != app.Value.ChangeNumber)
                {
                    changeNumber = app.Value;

                    CommandHandler.Send(Program.channelAnnounce, "{0}»{1} Bundled changelist {2}{3}{4} -{5} http://steamdb.info/changelist/{6}/",  Colors.BLUE, Colors.LIGHT_GRAY, Colors.OLIVE, changeNumber, Colors.LIGHT_GRAY, Colors.DARK_BLUE, changeNumber);
                }*/

                if (name.Equals(string.Empty))
                {
                    name = string.Format("{0}{1}{2}", Colors.DARK_GREEN, app.ID, Colors.NORMAL);
                }
                else
                {
                    name = string.Format("{0}{1}{2} - {3}", Colors.LIGHT_GRAY, app.ID, Colors.NORMAL, name);
                }

                IRC.SendAnnounce("  App: {0}{1}{2}",
                                 name,
                                 app.NeedsToken ? " (requires token)" : string.Empty,
                                 changeNumber != app.ChangeNumber ? string.Format(" - bundled changelist {0}{1}{2} -{3} http://steamdb.info/changelist/{4}/", Colors.OLIVE, app.ChangeNumber, Colors.NORMAL, Colors.DARK_BLUE, app.ChangeNumber) : string.Empty
                );
            }
        }

        private void ProcessSubChanges(uint changeNumber, Dictionary<uint, SteamApps.PICSChangesCallback.PICSChangeData> packageList)
        {
            string name;

            var important = packageList.Keys.Intersect(importantSubs);

            foreach (var package in important)
            {
                IRC.SendMain("Important package update: {0}{1}{2} -{3} http://steamdb.info/sub/{4}/#section_history", Colors.OLIVE, GetPackageName(package), Colors.NORMAL, Colors.DARK_BLUE, package);
            }

            foreach (var package in packageList.Values)
            {
                name = GetPackageName(package.ID);

                if (name.Equals(string.Empty))
                {
                    name = string.Format("{0}{1}{2}", Colors.GREEN, package.ID, Colors.NORMAL);
                }
                else
                {
                    name = string.Format("{0}{1}{2} - {3}", Colors.LIGHT_GRAY, package.ID, Colors.NORMAL, name);
                }

                IRC.SendAnnounce("  Package: {0}{1}{2}",
                                 name,
                                 package.NeedsToken ? " (requires token)" : string.Empty,
                                 changeNumber != package.ChangeNumber ? string.Format(" - bundled changelist {0}{1}{2} -{3} http://steamdb.info/changelist/{4}/", Colors.OLIVE, package.ChangeNumber, Colors.NORMAL, Colors.DARK_BLUE, package.ChangeNumber) : string.Empty
                );
            }
        }

        public static void PlayGame(SteamClient client, uint AppID)
        {
            var clientMsg = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayed);

            clientMsg.Body.games_played.Add(new CMsgClientGamesPlayed.GamePlayed
            {
                game_id = AppID
            });

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

                    IRC.SendMain("{0}{1}{2} item schema updated: {3}{4}{5} -{6} {7}", Colors.OLIVE, GetAppName(AppID), Colors.NORMAL, Colors.DARK_GRAY, msg.Body.item_schema_version.ToString("X4"), Colors.NORMAL, Colors.DARK_BLUE, msg.Body.items_game_url);
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

                    if (info.LastVersion != 0)
                    {
                        IRC.SendMain(message);
                    }

                    IRC.SendAnnounce(message);

                    info.LastVersion = msg.Body.version;

                    DbWorker.ExecuteNonQuery("INSERT INTO `GC` (`AppID`, `Status`, `Version`) VALUES(@AppID, @Status, @Version) ON DUPLICATE KEY UPDATE `Status` = @Status, `Version` = @Version",
                                             new MySqlParameter("@AppID", AppID),
                                             new MySqlParameter("@Status", GCConnectionStatus.GCConnectionStatus_HAVE_SESSION.ToString()),
                                             new MySqlParameter("@Version", msg.Body.version)
                    );
                }
            }
            else if (callback.EMsg == (uint)EGCBaseMsg.k_EMsgGCSystemMessage)
            {
                var msg = new ClientGCMsgProtobuf<CMsgSystemBroadcast>(callback.Message);

                Log.WriteInfo(string.Format("GC {0}", AppID), "Message: {0}", msg.Body.message);

                IRC.SendMain("{0}{1}{2} system message:{3} {4}", Colors.OLIVE, GetAppName(AppID), Colors.NORMAL, Colors.OLIVE, msg.Body.message);
            }
            else if (callback.EMsg == (uint)EGCBaseClientMsg.k_EMsgGCClientConnectionStatus || callback.EMsg == 4008 /* tf2's k_EMsgGCClientGoodbye */)
            {
                var msg = new ClientGCMsgProtobuf<CMsgConnectionStatus>(callback.Message);

                Log.WriteInfo(string.Format("GC {0}", AppID), "Status: {0}", msg.Body.status);

                string message = string.Format("{0}{1}{2} GC status:{3} {4}", Colors.OLIVE, GetAppName(AppID), Colors.NORMAL, Colors.OLIVE, msg.Body.status);

                if (info.LastStatus != msg.Body.status)
                {
                    IRC.SendMain(message);

                    info.LastStatus = msg.Body.status;
                }

                IRC.SendAnnounce(message);

                if (msg.Body.status == GCConnectionStatus.GCConnectionStatus_NO_SESSION)
                {
                    GameCoordinatorHello(AppID, gc);
                }

                DbWorker.ExecuteNonQuery("INSERT INTO `GC` (`AppID`, `Status`) VALUES(@AppID, @Status) ON DUPLICATE KEY UPDATE `Status` = @Status",
                                         new MySqlParameter("@AppID", AppID),
                                         new MySqlParameter("@Status", msg.Body.status.ToString())
                );
            }
        }

        public static void GameCoordinatorHello(uint AppID, SteamGameCoordinator gc)
        {
            var clientHello = new ClientGCMsgProtobuf<CMsgClientHello>((uint)EGCBaseClientMsg.k_EMsgGCClientHello);

            gc.Send(clientHello, AppID);
        }
    }
}
