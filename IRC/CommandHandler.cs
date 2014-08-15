/*
 * Copyright (c) 2013, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using Meebey.SmartIrc4net;
using MySql.Data.MySqlClient;
using SteamKit2;

namespace SteamDatabaseBackend
{
    public static class CommandHandler
    {
        public static readonly Dictionary<string, CommandData> Commands = new Dictionary<string, CommandData>
        {
            { "!help",      new CommandData(OnCommandHelp,       false, false) },
            { "!blog",      new CommandData(OnCommandBlog,       false, false) },
            { "!app",       new CommandData(OnCommandApp,        true,  false) },
            { "!sub",       new CommandData(OnCommandPackage,    true,  false) },
            { "!players",   new CommandData(OnCommandPlayers,    true,  false) },
            { "!eresult",   new CommandData(OnCommandEResult,    false, false) },
            { "!bins",      new CommandData(OnCommandBinaries,   false, false) },
            { "!important", new CommandData(OnCommandImportant,  false,  true) },
            { "!relogin",   new CommandData(OnCommandRelogin,    false,  true) },
        };

        public struct CommandData
        {
            public Action<CommandArguments> Callback;
            public bool RequiresSteam;
            public bool OpsOnly;

            public CommandData(Action<CommandArguments> callback, bool requiresSteam, bool opsOnly)
            {
                Callback = callback;
                RequiresSteam = requiresSteam;
                OpsOnly = opsOnly;
            }
        }

        public class CommandArguments
        {
            public string Message { get; set; }
            public IrcMessageData MessageData { get; set; }
            public SteamID ChatRoomID { get; set; }
            public SteamID SenderID { get; set; }

            public bool IsChatRoomCommand
            {
                get
                {
                    return this.ChatRoomID != null;
                }
            }
        }

        public static void ReplyToCommand(CommandArguments command, string message, params object[] args)
        {
            message = string.Format(message, args);

            if (command.IsChatRoomCommand)
            {
                Steam.Instance.Friends.SendChatRoomMessage(command.ChatRoomID, EChatEntryType.ChatMsg, string.Format(":dsham: {0}: {1}", Steam.Instance.Friends.GetFriendPersonaName(command.SenderID), Colors.StripColors(message)));
            }
            else
            {
                if (command.MessageData.Type == ReceiveType.ChannelMessage)
                {
                    message = string.Format("{0}{1}{2}: {3}", Colors.OLIVE, command.MessageData.Nick, Colors.NORMAL, message);
                }

                IRC.Instance.Client.SendReply(command.MessageData, message, Priority.High);
            }
        }

        public static void OnChannelMessage(object sender, IrcEventArgs e)
        {
            if (e.Data.Message[0] != '!')
            {
                return;
            }

            CommandData commandData;

            if (Commands.TryGetValue(e.Data.MessageArray[0], out commandData))
            {
                var input = e.Data.Message.Substring(e.Data.MessageArray[0].Length).Trim();

                var command = new CommandArguments
                {
                    MessageData = e.Data,
                    Message = input
                };

                if (commandData.RequiresSteam && !Steam.Instance.Client.IsConnected)
                {
                    ReplyToCommand(command, "Not connected to Steam.");

                    return;
                }
                else if (commandData.OpsOnly)
                {
                    // Check if user is in admin list or op in a channel
                    if (!Settings.Current.IRC.Admins.Contains(string.Format("{0}@{1}", e.Data.Ident, e.Data.Host)) && (e.Data.Type != ReceiveType.ChannelMessage || !IRC.IsSenderOp(e.Data)))
                    {
                        ReplyToCommand(command, "You're not an admin!");

                        return;
                    }
                }
                else if (SteamDB.IsBusy())
                {
                    ReplyToCommand(command, "The bot is currently busy.");

                    return;
                }

                Log.WriteInfo("IRC", "Handling command {0} for user {1} ({2}@{3}) in channel {4}", e.Data.MessageArray[0], e.Data.Nick, e.Data.Ident, e.Data.Host, e.Data.Channel);

                commandData.Callback(command);
            }
        }

        private static void OnCommandHelp(CommandArguments command)
        {
            if (command.Message.Length > 0)
            {
                return;
            }

            ReplyToCommand(command, "Available commands: {0}{1}", Colors.OLIVE, string.Join(string.Format("{0}, {1}", Colors.NORMAL, Colors.OLIVE), Commands.Keys));
        }

        private static void OnCommandBlog(CommandArguments command)
        {
            using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `ID`, `Slug`, `Title` FROM `Blog` WHERE `IsHidden` = 0 ORDER BY `ID` DESC LIMIT 1"))
            {
                if (Reader.Read())
                {
                    var slug = Reader.GetString("Slug");

                    if (slug.Length == 0)
                    {
                        slug = Reader.GetString("ID");
                    }

                    ReplyToCommand(command, "Latest blog post:{0} {1}{2} -{3} {4}", Colors.GREEN, Reader.GetString("Title"), Colors.NORMAL, Colors.DARK_BLUE, SteamDB.GetBlogURL(slug));

                    return;
                }
            }

            ReplyToCommand(command, "Something went wrong.");
        }

        private static void OnCommandApp(CommandArguments command)
        {
            if (command.Message.Length == 0)
            {
                ReplyToCommand(command, "Usage:{0} !app <appid or partial game name>", Colors.OLIVE);

                return;
            }

            uint appID;

            if (uint.TryParse(command.Message, out appID))
            {
                var apps = new List<uint>();

                apps.Add(appID);

                var jobID = Steam.Instance.Apps.PICSGetAccessTokens(apps, Enumerable.Empty<uint>());

                SteamProxy.Instance.IRCRequests.Add(new SteamProxy.IRCRequest
                {
                    JobID = jobID,
                    Target = appID,
                    Type = SteamProxy.IRCRequestType.TYPE_APP,
                    Command = command
                });
            }
            else
            {
                string name = string.Format("%{0}%", command.Message);

                using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `AppID` FROM `Apps` WHERE `Apps`.`StoreName` LIKE @Name OR `Apps`.`Name` LIKE @Name ORDER BY `LastUpdated` DESC LIMIT 1", new MySqlParameter("Name", name)))
                {
                    if (Reader.Read())
                    {
                        appID = Reader.GetUInt32("AppID");

                        var apps = new List<uint>();

                        apps.Add(appID);

                        var jobID = Steam.Instance.Apps.PICSGetAccessTokens(apps, Enumerable.Empty<uint>());

                        SteamProxy.Instance.IRCRequests.Add(new SteamProxy.IRCRequest
                            {
                                JobID = jobID,
                                Target = appID,
                                Type = SteamProxy.IRCRequestType.TYPE_APP,
                                Command = command
                            });
                    }
                    else
                    {
                        ReplyToCommand(command, "Nothing was found matching your request.");
                    }
                }
            }
        }

        private static void OnCommandPackage(CommandArguments command)
        {
            uint subID;

            if (command.Message.Length > 0 && uint.TryParse(command.Message, out subID))
            {
                var jobID = Steam.Instance.Apps.PICSGetProductInfo(null, subID, false, false);

                SteamProxy.Instance.IRCRequests.Add(new SteamProxy.IRCRequest
                {
                    JobID = jobID,
                    Target = subID,
                    Type = SteamProxy.IRCRequestType.TYPE_SUB,
                    Command = command
                });
            }
            else
            {
                ReplyToCommand(command, "Usage:{0} !sub <subid>", Colors.OLIVE);
            }
        }

        private static void OnCommandPlayers(CommandArguments command)
        {
            if (command.Message.Length == 0)
            {
                ReplyToCommand(command, "Usage:{0} !players <appid or partial game name>", Colors.OLIVE);

                return;
            }

            uint appID;

            if (uint.TryParse(command.Message, out appID))
            {
                var jobID = Steam.Instance.UserStats.GetNumberOfCurrentPlayers(appID);

                SteamProxy.Instance.IRCRequests.Add(new SteamProxy.IRCRequest
                {
                    JobID = jobID,
                    Target = appID,
                    Command = command
                });
            }
            else
            {
                string name = string.Format("%{0}%", command.Message);

                // Ugh, have to filter out "games" that have "Demo" in their name, because valve cannot into consistency, some servers and demos are marked as games
                using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `AppID` FROM `Apps` LEFT JOIN `AppsTypes` ON `Apps`.`AppType` = `AppsTypes`.`AppType` WHERE `AppsTypes`.`Name` IN ('game', 'application') AND (`Apps`.`StoreName` LIKE @Name OR `Apps`.`Name` LIKE @Name) AND `Apps`.`Name` NOT LIKE '% Demo' AND `Apps`.`Name` NOT LIKE '%Dedicated Server%' ORDER BY `LastUpdated` DESC LIMIT 1", new MySqlParameter("Name", name)))
                {
                    if (Reader.Read())
                    {
                        appID = Reader.GetUInt32("AppID");

                        var jobID = Steam.Instance.UserStats.GetNumberOfCurrentPlayers(appID);

                        SteamProxy.Instance.IRCRequests.Add(new SteamProxy.IRCRequest
                        {
                            JobID = jobID,
                            Target = appID,
                            Command = command
                        });
                    }
                    else
                    {
                        ReplyToCommand(command, "Nothing was found matching your request.");
                    }
                }
            }
        }

        private static void OnCommandEResult(CommandArguments command)
        {
            if (command.Message.Length == 0)
            {
                ReplyToCommand(command, "Usage:{0} !eresult <number>", Colors.OLIVE);

                return;
            }

            if (command.Message.Equals("consistency", StringComparison.CurrentCultureIgnoreCase))
            {
                ReplyToCommand(command, "{0}Consistency{1} = {2}Valve", Colors.LIGHT_GRAY, Colors.NORMAL, Colors.RED);

                return;
            }

            int eResult;

            if (!int.TryParse(command.Message, out eResult))
            {
                try
                {
                    eResult = (int)Enum.Parse(typeof(EResult), command.Message, true);
                }
                catch
                {
                    eResult = -1;
                }
            }

            if(!Enum.IsDefined(typeof(EResult), eResult))
            {
                ReplyToCommand(command, "Unknown or invalid EResult.");

                return;
            }

            ReplyToCommand(command, "{0}{1}{2} = {3}", Colors.LIGHT_GRAY, eResult, Colors.NORMAL, (EResult)eResult);
        }

        private static void OnCommandBinaries(CommandArguments command)
        {
            string cdn = "https://steamcdn-a.akamaihd.net/client/";

            using (var webClient = new WebClient())
            {
                webClient.DownloadDataCompleted += delegate(object sender, DownloadDataCompletedEventArgs e)
                {
                    var kv = new KeyValue();

                    using (var ms = new MemoryStream(e.Result))
                    {
                        try
                        {
                            kv.ReadAsText(ms);
                        }
                        catch
                        {
                            ReplyToCommand(command, "Something went horribly wrong and keyvalue parser died.");

                            return;
                        }
                    }

                    if (kv["bins_osx"].Children.Count == 0)
                    {
                        ReplyToCommand(command, "Failed to find binaries in parsed response.");

                        return;
                    }

                    kv = kv["bins_osx"];

                    ReplyToCommand(command, "You're on your own:{0} {1}{2} {3}({4} MB)", Colors.DARK_BLUE, cdn, kv["file"].AsString(), Colors.DARK_GRAY, (kv["size"].AsLong() / 1048576.0).ToString("0.###"));
                };

                webClient.DownloadDataAsync(new Uri(string.Format("{0}steam_client_publicbeta_osx?_={1}", cdn, DateTime.UtcNow.Ticks)));
            }
        }

        private static void OnCommandRelogin(CommandArguments command)
        {
            if (Steam.Instance.Client.IsConnected)
            {
                Steam.Instance.Client.Connect();
            }

            foreach (var idler in Program.GCIdlers)
            {
                if (idler.Client.IsConnected)
                {
                    idler.Client.Connect();
                }
            }

            ReplyToCommand(command, "Reconnect forced.");
        }

        private static void OnCommandImportant(CommandArguments command)
        {
            var s = command.Message.Split(' ');
            var count = s.Count();

            if (count > 0)
            {
                switch (s[0])
                {
                    case "reload":
                    {
                        SteamProxy.Instance.ReloadImportant(command);

                        return;
                    }

                    case "add":
                    {
                        if (count < 3)
                        {
                            break;
                        }

                        uint id;

                        if (!uint.TryParse(s[2], out id))
                        {
                            break;
                        }

                        switch(s[1])
                        {
                            case "app":
                            {
                                if (SteamProxy.Instance.ImportantApps.Contains(id))
                                {
                                    ReplyToCommand(command, "App {0}{1}{2} ({3}) is already important.", Colors.GREEN, id, Colors.NORMAL, SteamProxy.GetAppName(id));
                                }
                                else
                                {
                                    SteamProxy.Instance.ImportantApps.Add(id);

                                    DbWorker.ExecuteNonQuery("INSERT INTO `ImportantApps` (`AppID`, `Announce`) VALUES (@AppID, 1) ON DUPLICATE KEY UPDATE `Announce` = 1", new MySqlParameter("AppID", id));

                                    ReplyToCommand(command, "Marked app {0}{1}{2} ({3}) as important.", Colors.GREEN, id, Colors.NORMAL, SteamProxy.GetAppName(id));
                                }

                                return;
                            }
                            case "sub":
                            {
                                if (SteamProxy.Instance.ImportantSubs.Contains(id))
                                {
                                    ReplyToCommand(command, "Package {0}{1}{2} ({3}) is already important.", Colors.GREEN, id, Colors.NORMAL, SteamProxy.GetPackageName(id));
                                }
                                else
                                {
                                    SteamProxy.Instance.ImportantSubs.Add(id);

                                    DbWorker.ExecuteNonQuery("INSERT INTO `ImportantSubs` (`SubID`) VALUES (@SubID)", new MySqlParameter("SubID", id));

                                    ReplyToCommand(command, "Marked package {0}{1}{2} ({3}) as important.", Colors.GREEN, id, Colors.NORMAL, SteamProxy.GetPackageName(id));
                                }

                                return;
                            }
                        }

                        break;
                    }

                    case "remove":
                    {
                        if (count < 3)
                        {
                            break;
                        }

                        uint id;

                        if (!uint.TryParse(s[2], out id))
                        {
                            break;
                        }

                        switch(s[1])
                        {
                            case "app":
                            {
                                if (!SteamProxy.Instance.ImportantApps.Contains(id))
                                {
                                    ReplyToCommand(command, "App {0}{1}{2} ({3}) is not important.",  Colors.GREEN, id, Colors.NORMAL, SteamProxy.GetAppName(id));
                                }
                                else
                                {
                                    SteamProxy.Instance.ImportantApps.Remove(id);

                                    DbWorker.ExecuteNonQuery("UPDATE `ImportantApps` SET `Announce` = 0 WHERE `AppID` = @AppID", new MySqlParameter("AppID", id));

                                    ReplyToCommand(command, "Removed app {0}{1}{2} ({3}) from the important list.", Colors.GREEN, id, Colors.NORMAL, SteamProxy.GetAppName(id));
                                }

                                return;
                            }
                            case "sub":
                            {
                                if (!SteamProxy.Instance.ImportantSubs.Contains(id))
                                {
                                    ReplyToCommand(command, "Package {0}{1}{2} ({3}) is not important.", Colors.GREEN, id, Colors.NORMAL, SteamProxy.GetPackageName(id));
                                }
                                else
                                {
                                    SteamProxy.Instance.ImportantSubs.Remove(id);

                                    DbWorker.ExecuteNonQuery("DELETE FROM `ImportantSubs` WHERE `SubID` = @SubID", new MySqlParameter("SubID", id));

                                    ReplyToCommand(command, "Removed package {0}{1}{2} ({3}) from the important list.", Colors.GREEN, id, Colors.NORMAL, SteamProxy.GetPackageName(id));
                                }

                                return;
                            }
                        }

                        break;
                    }
                }
            }

            ReplyToCommand(command, "Usage:{0} !important reload {1}or{2} !important <add/remove> <app/sub> <id>", Colors.OLIVE, Colors.NORMAL, Colors.OLIVE);
        }
    }
}
