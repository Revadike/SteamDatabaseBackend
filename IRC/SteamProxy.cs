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

namespace SteamDatabaseBackend
{
    public class SteamProxy
    {
        private static SteamProxy _instance = new SteamProxy();
        public static SteamProxy Instance { get { return _instance; } }

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
            public uint DepotID { get; set; }
        }

        private static readonly SteamID SteamLUG = new SteamID(103582791431044413UL);
        private static readonly string ChannelSteamLUG = "#steamlug";

        public List<IRCRequest> IRCRequests { get; private set; }
        public List<uint> ImportantApps { get; private set; }
        private List<uint> ImportantSubs;

        SteamProxy()
        {
            IRCRequests = new List<IRCRequest>();

            ImportantApps = new List<uint>();
            ImportantSubs = new List<uint>();
        }

        public void ReloadImportant(string channel = "", string nickName = "")
        {
            using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `AppID` FROM `ImportantApps` WHERE `Announce` = 1"))
            {
                ImportantApps.Clear();

                while (Reader.Read())
                {
                    ImportantApps.Add(Reader.GetUInt32("AppID"));
                }
            }

            using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `SubID` FROM `ImportantSubs`"))
            {
                ImportantSubs.Clear();

                while (Reader.Read())
                {
                    ImportantSubs.Add(Reader.GetUInt32("SubID"));
                }
            }

            if (string.IsNullOrEmpty(channel))
            {
                Log.WriteInfo("IRC Proxy", "Loaded {0} important apps and {1} packages", ImportantApps.Count, ImportantSubs.Count);
            }
            else
            {
                IRC.Send(channel, "{0}{1}{2}: Reloaded {3} important apps and {4} packages", Colors.OLIVE, nickName, Colors.NORMAL, ImportantApps.Count, ImportantSubs.Count);
            }
        }

        public static string GetPackageName(uint subID)
        {
            string name = string.Empty;

            using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `Name`, `StoreName` FROM `Subs` WHERE `SubID` = @SubID", new MySqlParameter("SubID", subID)))
            {
                if (Reader.Read())
                {
                    name = DbWorker.GetString("Name", Reader);

                    if (name.StartsWith("Steam Sub", StringComparison.Ordinal))
                    {
                        string nameStore = DbWorker.GetString("StoreName", Reader);

                        if (!string.IsNullOrEmpty(nameStore))
                        {
                            name = string.Format("{0} {1}({2}){3}", name, Colors.DARK_GRAY, nameStore, Colors.NORMAL);
                        }
                    }
                }
            }

            return name;
        }

        public static string GetAppName(uint appID)
        {
            string name = string.Empty;
            string nameStore = string.Empty;

            using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `Name`, `StoreName` FROM `Apps` WHERE `AppID` = @AppID", new MySqlParameter("AppID", appID)))
            {
                if (Reader.Read())
                {
                    name = DbWorker.GetString("Name", Reader);
                    nameStore = DbWorker.GetString("StoreName", Reader);
                }
            }

            if (string.IsNullOrEmpty(name) || name.StartsWith("ValveTestApp", StringComparison.Ordinal) || name.StartsWith("SteamDB Unknown App", StringComparison.Ordinal))
            {
                if (!string.IsNullOrEmpty(nameStore))
                {
                    return string.Format("{0} {1}({2}){3}", name, Colors.DARK_GRAY, nameStore, Colors.NORMAL);
                }

                using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `NewValue` FROM `AppsHistory` WHERE `AppID` = @AppID AND `Action` = 'created_info' AND `Key` = 1 LIMIT 1", new MySqlParameter("AppID", appID)))
                {
                    if (Reader.Read())
                    {
                        nameStore = DbWorker.GetString("NewValue", Reader);

                        if (string.IsNullOrEmpty(name))
                        {
                            name = string.Format("AppID {0}", appID);
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

            string groupName = callback.ClanName;
            string message;

            if (string.IsNullOrEmpty(groupName))
            {
                groupName = Steam.Instance.Friends.GetClanName(callback.ClanID);

                // Check once more, because that can fail too
                if (string.IsNullOrEmpty(groupName))
                {
                    groupName = "Group";

                    Log.WriteError("IRC Proxy", "ClanID: {0} - no group name", callback.ClanID);
                }
            }

            foreach (var announcement in callback.Announcements)
            {
                message = string.Format("{0}{1}{2} announcement: {3}{4}{5} -{6} http://steamcommunity.com/gid/{7}/announcements/detail/{8}", Colors.OLIVE, groupName, Colors.NORMAL, Colors.GREEN, announcement.Headline, Colors.NORMAL, Colors.DARK_BLUE, callback.ClanID, announcement.ID);

                IRC.SendMain(message);

                // Additionally send announcements to steamlug channel
                if (callback.ClanID.Equals(SteamLUG))
                {
                    IRC.Send(ChannelSteamLUG, message);
                }

                Log.WriteInfo("Group Announcement", "{0} \"{1}\"", groupName, announcement.Headline);
            }

            foreach (var groupEvent in callback.Events)
            {
                if (groupEvent.JustPosted)
                {
                    message = string.Format("{0}{1}{2} event: {3}{4}{5} -{6} http://steamcommunity.com/gid/{7}/events/{8}", Colors.OLIVE, groupName, Colors.NORMAL, Colors.GREEN, groupEvent.Headline, Colors.NORMAL, Colors.DARK_BLUE, callback.ClanID, groupEvent.ID);

                    // Send events only to steamlug channel
                    if (callback.ClanID.Equals(SteamLUG))
                    {
                        IRC.Send(ChannelSteamLUG, message);
                    }
                    else
                    {
                        IRC.SendMain(message);
                    }

                    Log.WriteInfo("Group Announcement", "{0} Event \"{1}\"", groupName, groupEvent.Headline);
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

                    if (string.IsNullOrEmpty(name))
                    {
                        name = string.Format("AppID {0}", request.Target);
                    }
                }

                using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `AppID` FROM `ImportantApps` WHERE `Graph` = 1 AND `AppID` = @AppID", new MySqlParameter("AppID", request.Target)))
                {
                    if (Reader.Read())
                    {
                        graph = string.Format("{0} - graph:{1} {2}", Colors.NORMAL, Colors.DARK_BLUE, SteamDB.GetGraphURL(request.Target));
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
                string name = string.Format("SubID {0}", info.ID);

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

                IRC.Send(request.Channel, "{0}{1}{2}: Dump for {3}{4}{5} -{6} {7}{8}{9}",
                                    Colors.OLIVE, request.Requester, Colors.NORMAL,
                                    Colors.OLIVE, name, Colors.NORMAL,
                                    Colors.DARK_BLUE, SteamDB.GetRawPackageURL(info.ID), Colors.NORMAL,
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

                IRC.Send(request.Channel, "{0}{1}{2}: Dump for {3}{4}{5} -{6} {7}{8}{9}",
                                    Colors.OLIVE, request.Requester, Colors.NORMAL,
                                    Colors.OLIVE, name, Colors.NORMAL,
                                    Colors.DARK_BLUE, SteamDB.GetRawAppURL(info.ID), Colors.NORMAL,
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
            string Message = string.Format("Received changelist {0}{1}{2} with {3}{4}{5} apps and {6}{7}{8} packages -{9} {10}",
                                           Colors.OLIVE, changeNumber, Colors.NORMAL,
                                           callback.AppChanges.Count >= 10 ? Colors.YELLOW : Colors.OLIVE, callback.AppChanges.Count, Colors.NORMAL,
                                           callback.PackageChanges.Count >= 10 ? Colors.YELLOW : Colors.OLIVE, callback.PackageChanges.Count, Colors.NORMAL,
                                           Colors.DARK_BLUE, SteamDB.GetChangelistURL(changeNumber)
                             );

            if (callback.AppChanges.Count >= 50 || callback.PackageChanges.Count >= 50)
            {
                IRC.SendMain(Message);
            }

            IRC.SendAnnounce("{0}»{1} {2}",  Colors.RED, Colors.NORMAL, Message);

            // If this changelist is very big, freenode will hate us forever if we decide to print all that stuff
            bool importantOnly = callback.AppChanges.Count + callback.PackageChanges.Count > 1000;

            if (callback.AppChanges.Count > 0)
            {
                ProcessAppChanges(changeNumber, callback.AppChanges, importantOnly);
            }

            if (callback.PackageChanges.Count > 0)
            {
                ProcessSubChanges(changeNumber, callback.PackageChanges, importantOnly);
            }

            if (importantOnly)
            {
                IRC.SendAnnounce("{0}  This changelist is too big to be printed in IRC, please view it on our website", Colors.RED);
            }
        }

        private void ProcessAppChanges(uint changeNumber, Dictionary<uint, SteamApps.PICSChangesCallback.PICSChangeData> appList, bool importantOnly = false)
        {
            string name;

            var important = appList.Keys.Intersect(ImportantApps);

            foreach (var app in important)
            {
                name = GetAppName(app);

                IRC.SendMain("Important app update: {0}{1}{2} -{3} {4}", Colors.OLIVE, name, Colors.NORMAL, Colors.DARK_BLUE, SteamDB.GetAppURL(app, "history"));
            }

            if (importantOnly)
            {
                return;
            }

            foreach (var app in appList.Values)
            {
                name = GetAppName(app.ID);

                /*if (changeNumber != app.Value.ChangeNumber)
                {
                    changeNumber = app.Value;

                    CommandHandler.Send(Program.channelAnnounce, "{0}»{1} Bundled changelist {2}{3}{4} -{5} {6}",  Colors.BLUE, Colors.LIGHT_GRAY, Colors.OLIVE, changeNumber, Colors.LIGHT_GRAY, Colors.DARK_BLUE, SteamDB.GetChangelistURL(changeNumber));
                }*/

                if (string.IsNullOrEmpty(name))
                {
                    name = string.Format("{0}{1}{2}", Colors.GREEN, app.ID, Colors.NORMAL);
                }
                else
                {
                    name = string.Format("{0}{1}{2} - {3}", Colors.LIGHT_GRAY, app.ID, Colors.NORMAL, name);
                }

                IRC.SendAnnounce("  App: {0}{1}{2}",
                                 name,
                                 app.NeedsToken ? " (requires token)" : string.Empty,
                                 changeNumber != app.ChangeNumber ? string.Format(" - bundled changelist {0}{1}{2} -{3} {4}", Colors.OLIVE, app.ChangeNumber, Colors.NORMAL, Colors.DARK_BLUE, SteamDB.GetChangelistURL(app.ChangeNumber)) : string.Empty
                );
            }
        }

        private void ProcessSubChanges(uint changeNumber, Dictionary<uint, SteamApps.PICSChangesCallback.PICSChangeData> packageList, bool importantOnly = false)
        {
            string name;

            var important = packageList.Keys.Intersect(ImportantSubs);

            foreach (var package in important)
            {
                name = GetPackageName(package);

                IRC.SendMain("Important package update: {0}{1}{2} -{3} {4}", Colors.OLIVE, name, Colors.NORMAL, Colors.DARK_BLUE, SteamDB.GetPackageURL(package, "history"));
            }

            if (importantOnly)
            {
                return;
            }

            foreach (var package in packageList.Values)
            {
                name = GetPackageName(package.ID);

                if (string.IsNullOrEmpty(name))
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
                                 changeNumber != package.ChangeNumber ? string.Format(" - bundled changelist {0}{1}{2} -{3} {4}", Colors.OLIVE, package.ChangeNumber, Colors.NORMAL, Colors.DARK_BLUE, SteamDB.GetChangelistURL(package.ChangeNumber)) : string.Empty
                );
            }
        }
    }
}
