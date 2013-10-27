/*
 * Copyright (c) 2013, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Linq;
using System.Collections.Generic;
using Meebey.SmartIrc4net;
using MySql.Data.MySqlClient;
using SteamKit2;
using SteamKit2.Unified.Internal;

namespace SteamDatabaseBackend
{
    public static class CommandHandler
    {
        public static readonly Dictionary<string, Action<CommandArguments>> Commands = new Dictionary<string, Action<CommandArguments>>
        {
            { "!help", OnCommandHelp },
            { "!app", OnCommandApp },
            { "!sub", OnCommandPackage },
            { "!games", OnCommandGames },
            { "!players", OnCommandPlayers },
            { "!reload", OnCommandReload },
            { "!force", OnCommandForce }
        };

        public class CommandArguments
        {
            public string[] MessageArray { get; set; }
            public string Channel { get; set; }
            public string Nickname { get; set; }
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
            if (command.IsChatRoomCommand)
            {
                Steam.Instance.Friends.SendChatRoomMessage(command.ChatRoomID, EChatEntryType.ChatMsg, Colors.StripColors(string.Format(message, args)));
            }
            else
            {
                IRC.Send(command.Channel, message, args);
            }
        }

        public static void OnChannelMessage(object sender, IrcEventArgs e)
        {
            if (e.Data.Message[0] != '!')
            {
                return;
            }

            Action<CommandArguments> callbackFunction;

            if (Commands.TryGetValue(e.Data.MessageArray[0], out callbackFunction))
            {
                Log.WriteInfo("IRC", "Handling command {0} for user {1} in channel {2}", e.Data.MessageArray[0], e.Data.Nick, e.Data.Channel);

                callbackFunction(new CommandArguments
                {
                    Channel = e.Data.Channel,
                    Nickname = e.Data.Nick,
                    MessageArray = e.Data.MessageArray
                });
            }
        }

        private static void OnCommandHelp(CommandArguments command)
        {
            ReplyToCommand(command, "{0}{1}{2}: Available commands: {3}{4}", Colors.OLIVE, command.Nickname, Colors.NORMAL, Colors.OLIVE, string.Join(string.Format("{0}, {1}", Colors.NORMAL, Colors.OLIVE), Commands.Keys));
        }

        private static void OnCommandApp(CommandArguments command)
        {
            uint appID;

            if (command.MessageArray.Length == 2 && uint.TryParse(command.MessageArray[1], out appID))
            {
                var jobID = Steam.Instance.Apps.PICSGetProductInfo(appID, null, false, false);

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
                ReplyToCommand(command, "Usage:{0} !app <appid>", Colors.OLIVE);
            }
        }

        private static void OnCommandPackage(CommandArguments command)
        {
            uint subID;

            if (command.MessageArray.Length == 2 && uint.TryParse(command.MessageArray[1], out subID))
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
            if (command.MessageArray.Length < 2)
            {
                ReplyToCommand(command, "Usage:{0} !players <appid or partial game name>", Colors.OLIVE);

                return;
            }

            uint appID;

            if (uint.TryParse(command.MessageArray[1], out appID))
            {
                var jobID = Steam.Instance.UserStats.GetNumberOfCurrentPlayers(appID);

                SteamProxy.Instance.IRCRequests.Add(new SteamProxy.IRCRequest
                {
                    JobID = jobID,
                    Target = appID,
                    Command = command
                });
            }
            else if (command.MessageArray[1].ToLower().Equals("\x68\x6C\x33"))
            {
                ReplyToCommand(command, "{0}{1}{2}: People playing {3}{4}{5} right now: {6}{7}", Colors.OLIVE, command.Nickname, Colors.NORMAL, Colors.OLIVE, "\x48\x61\x6C\x66\x2D\x4C\x69\x66\x65\x20\x33", Colors.NORMAL, Colors.YELLOW, "\x7e\x34\x30\x30");
            }
            else
            {
                string name = string.Format("%{0}%", string.Join(" ", command.MessageArray.Skip(1)).Trim());

                using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `AppID` FROM `Apps` LEFT JOIN `AppsTypes` ON `Apps`.`AppType` = `AppsTypes`.`AppType` WHERE `AppsTypes`.`Name` IN ('game', 'application') AND (`Apps`.`StoreName` LIKE @Name OR `Apps`.`Name` LIKE @Name) ORDER BY `LastUpdated` DESC LIMIT 1", new MySqlParameter("Name", name)))
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
                        ReplyToCommand(command, "{0}{1}{2}: Nothing was found matching your request", Colors.OLIVE, command.Nickname, Colors.NORMAL);
                    }
                }
            }
        }

        private static void OnCommandGames(CommandArguments command)
        {
            SteamID steamID = null;

            if (command.MessageArray.Length > 1)
            {
                string input = command.MessageArray[1];
                ulong uSteamID;

                if (input.StartsWith("STEAM_", StringComparison.OrdinalIgnoreCase))
                {
                    steamID = new SteamID(input, EUniverse.Public);
                }
                else if (ulong.TryParse(input, out uSteamID))
                {
                    steamID = new SteamID(uSteamID);
                }

                if (steamID == null || steamID == 0)
                {
                    ReplyToCommand(command, "{0}{1}{2}: That doesn't look like a valid SteamID", Colors.OLIVE, command.Nickname, Colors.NORMAL);

                    return;
                }
            }
            else if (command.IsChatRoomCommand)
            {
                steamID = command.SenderID;
            }
            else
            {
                ReplyToCommand(command, "Usage:{0} !games <steamid>", Colors.OLIVE);

                return;
            }

            var request = new CPlayer_GetOwnedGames_Request();
            request.steamid = steamID;
            request.include_played_free_games = true;

            JobID jobID = Steam.Instance.Unified.SendMessage("Player.GetOwnedGames#1", request);

            SteamProxy.Instance.IRCRequests.Add(new SteamProxy.IRCRequest
            {
                JobID = jobID,
                SteamID = steamID,
                Command = command
            });
        }

        private static void OnCommandReload(CommandArguments command)
        {
            if (command.IsChatRoomCommand)
            {
                ReplyToCommand(command, "{0}: This command can only be used in IRC", command.Nickname);

                return;
            }

            if (IRC.IsSenderOp(command.Channel, command.Nickname))
            {
                SteamProxy.Instance.ReloadImportant(command.Channel, command.Nickname);
            }
        }

        private static void OnCommandForce(CommandArguments command)
        {
            if (command.IsChatRoomCommand)
            {
                ReplyToCommand(command, "{0}: This command can only be used in IRC", command.Nickname);

                return;
            }

            if (!IRC.IsSenderOp(command.Channel, command.Nickname))
            {
                return;
            }

            if (command.MessageArray.Length == 3)
            {
                uint target;

                if (!uint.TryParse(command.MessageArray[2], out target))
                {
                    ReplyToCommand(command, "Usage:{0} !force [<app/sub/changelist> <target>]", Colors.OLIVE);

                    return;
                }

                switch (command.MessageArray[1])
                {
                    case "app":
                    {
                        Steam.Instance.Apps.PICSGetProductInfo(target, null, false, false);

                        ReplyToCommand(command, "{0}{1}{2}: Forced update for AppID {3}{4}", Colors.OLIVE, command.Nickname, Colors.NORMAL, Colors.OLIVE, target);

                        break;
                    }

                    case "sub":
                    {
                        Steam.Instance.Apps.PICSGetProductInfo(null, target, false, false);

                        ReplyToCommand(command, "{0}{1}{2}: Forced update for SubID {3}{4}", Colors.OLIVE, command.Nickname, Colors.NORMAL, Colors.OLIVE, target);

                        break;
                    }

#if DEBUG
                    case "changelist":
                    {
                        if (Math.Abs(Steam.Instance.PreviousChange - target) > 100)
                        {
                            IRC.Send(e.Data.Channel, "Changelist difference is too big, will not execute");

                            break;
                        }

                        Steam.Instance.PreviousChange = target;

                        Steam.Instance.GetPICSChanges();

                        IRC.Send(e.Data.Channel, "{0}{1}{2}: Requested changes since changelist {3}{4}", Colors.OLIVE, e.Data.Nick, Colors.NORMAL, Colors.OLIVE, target);

                        break;
                    }
#endif

                    default:
                    {
                        ReplyToCommand(command, "Usage:{0} !force [<app/sub/changelist> <target>]", Colors.OLIVE);

                        break;
                    }
                }
            }
            else if (command.MessageArray.Length == 1)
            {
                Steam.Instance.GetPICSChanges();

                ReplyToCommand(command, "{0}{1}{2}: Forced a check", Colors.OLIVE, command.Nickname, Colors.NORMAL);
            }
            else
            {
                ReplyToCommand(command, "Usage:{0} !force [<app/sub/changelist> <target>]", Colors.OLIVE);
            }
        }
    }
}
