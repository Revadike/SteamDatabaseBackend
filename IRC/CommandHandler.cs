/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using Meebey.SmartIrc4net;
using SteamKit2;

namespace SteamDatabaseBackend
{
    class CommandHandler
    {
        private List<Command> RegisteredCommands;

        public CommandHandler()
        {
            RegisteredCommands = new List<Command>();

            RegisteredCommands.Add(new BlogCommand());
            RegisteredCommands.Add(new PlayersCommand());
            RegisteredCommands.Add(new AppCommand());
            RegisteredCommands.Add(new PackageCommand());
            RegisteredCommands.Add(new SteamIDCommand());
            RegisteredCommands.Add(new EnumCommand());
            RegisteredCommands.Add(new BinariesCommand());
            RegisteredCommands.Add(new ImportantCommand());
            RegisteredCommands.Add(new ReloginCommand());

            // Register help command last so we can pass the list of the commands
            RegisteredCommands.Add(new HelpCommand(RegisteredCommands));

            Log.WriteInfo("CommandHandler", "Registered {0} commands", RegisteredCommands.Count);
        }

        public static void ReplyToCommand(CommandArguments command, string message, params object[] args)
        {
            message = string.Format(message, args);

            if (command.IsChatRoomCommand)
            {
                Steam.Instance.Friends.SendChatRoomMessage(command.ChatRoomID, EChatEntryType.ChatMsg, string.Format(":dsham: {0}: {1}", Steam.Instance.Friends.GetFriendPersonaName(command.SenderID), Colors.StripColors(message)));
            }
            else if (command.IsSteamCommand)
            {
                Steam.Instance.Friends.SendChatMessage(command.SenderID, EChatEntryType.ChatMsg, Colors.StripColors(message));
            }
            else
            {
                if (command.MessageData.Type == ReceiveType.ChannelMessage)
                {
                    message = string.Format("{0}{1}{2}: {3}", Colors.OLIVE, command.MessageData.Nick, Colors.NORMAL, message);
                }

                IRC.Instance.SendReply(command.MessageData, message, Priority.High);
            }
        }

        public void OnIRCMessage(object sender, IrcEventArgs e)
        {
            if (e.Data.Message[0] != '!')
            {
                return;
            }

            var command = RegisteredCommands.FirstOrDefault(cmd => cmd.Trigger.Equals(e.Data.MessageArray[0]));

            if (command == null)
            {
                return;
            }

            var input = e.Data.Message.Substring(e.Data.MessageArray[0].Length).Trim();

            var commandData = new CommandArguments
            {
                MessageData = e.Data,
                Message = input
            };

            if (command.IsSteamCommand && !Steam.Instance.Client.IsConnected)
            {
                ReplyToCommand(commandData, "Not connected to Steam.");

                return;
            }
            else if (command.IsAdminCommand)
            {
                // Check if user is in admin list or op in a channel
                if (!Settings.Current.IRC.Admins.Contains(string.Format("{0}@{1}", e.Data.Ident, e.Data.Host)) && (e.Data.Type != ReceiveType.ChannelMessage || !IRC.Instance.IsSenderOp(e.Data)))
                {
                    ReplyToCommand(commandData, "You're not an admin!");

                    return;
                }
            }
            else if (SteamDB.IsBusy())
            {
                ReplyToCommand(commandData, "The bot is currently busy.");

                return;
            }

            Log.WriteInfo("CommandHandler", "Handling IRC command {0} for user {1} ({2}@{3}) in channel {4}", e.Data.MessageArray[0], e.Data.Nick, e.Data.Ident, e.Data.Host, e.Data.Channel);

            TryCommand(command, commandData);
        }

        public void OnSteamFriendMessage(SteamFriends.FriendMsgCallback callback)
        {
            if (callback.EntryType != EChatEntryType.ChatMsg        // Is chat message
            ||  callback.Sender == Steam.Instance.Client.SteamID    // Is not sent by the bot
            ||  callback.Message[0] != '!'                          // Starts with !
            ||  callback.Message.Contains('\n')                     // Does not contain new lines
            )
            {
                return;
            }

            HandleSteamMessage(callback.Sender, callback.Message);

            Log.WriteInfo("CommandHandler", "Handling Steam command {0} for user {1}", callback.Message, callback.Sender);
        }

        public void OnSteamChatMessage(SteamFriends.ChatMsgCallback callback)
        {
            if (callback.ChatMsgType != EChatEntryType.ChatMsg      // Is chat message
            ||  callback.ChatterID == Steam.Instance.Client.SteamID // Is not sent by the bot
            ||  callback.Message[0] != '!'                          // Starts with !
            ||  callback.Message.Contains('\n')                     // Does not contain new lines
            )
            {
                return;
            }

            HandleSteamMessage(callback.ChatterID, callback.Message, callback.ChatRoomID);

            Log.WriteInfo("CommandHandler", "Handling Steam command {0} for user {1} in chatroom {2}", callback.Message, callback.ChatterID, callback.ChatRoomID);
        }

        private void HandleSteamMessage(SteamID sender, string message, SteamID chatRoom = null)
        {
            var i = message.IndexOf(' ');
            var inputCommand = i == -1 ? message : message.Substring(0, i);

            var command = RegisteredCommands.FirstOrDefault(cmd => cmd.Trigger.Equals(inputCommand));

            if (command == null)
            {
                return;
            }

            var input = i == -1 ? string.Empty : message.Substring(i).Trim();

            var commandData = new CommandArguments
            {
                SenderID = sender,
                ChatRoomID = chatRoom,
                Message = input
            };

            if (command.IsAdminCommand)
            {
                CommandHandler.ReplyToCommand(commandData, "This command can only be used in IRC.");

                return;
            }
            else if (SteamDB.IsBusy())
            {
                CommandHandler.ReplyToCommand(commandData, "The bot is currently busy.");

                return;
            }

            TryCommand(command, commandData);
        }

        private static void TryCommand(Command command, CommandArguments commandData)
        {
            try
            {
                command.OnCommand(commandData);
            }
            catch (Exception e)
            {
                Log.WriteError("CommandHandler", "Exception while executing a command: {0}\n{1}", e.Message, e.StackTrace);

                ReplyToCommand(commandData, "Exception: {0}", e.Message);
            }
        }
    }
}
