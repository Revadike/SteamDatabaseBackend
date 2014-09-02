/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using NetIrc2.Events;
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

            switch (command.CommandType)
            {
                case ECommandType.IRC:
                {
                    var isChannelMessage = IRC.IsRecipientChannel(command.Recipient);

                    if (isChannelMessage)
                    {
                        message = string.Format("{0}{1}{2}: {3}", Colors.OLIVE, command.SenderIdentity.Nickname, Colors.NORMAL, message);
                    }

                    IRC.Instance.SendReply(isChannelMessage ? command.Recipient : command.SenderIdentity.Nickname.ToString(), message);

                    break;
                }

                case ECommandType.SteamChatRoom:
                {
                    Steam.Instance.Friends.SendChatRoomMessage(command.ChatRoomID, EChatEntryType.ChatMsg, string.Format(":dsham: {0}: {1}", Steam.Instance.Friends.GetFriendPersonaName(command.SenderID), Colors.StripColors(message)));
                    
                    break;
                }

                case ECommandType.SteamIndividual:
                {
                    Steam.Instance.Friends.SendChatMessage(command.SenderID, EChatEntryType.ChatMsg, Colors.StripColors(message));
                    
                    break;
                }
            }
        }

        public void OnIRCMessage(object sender, ChatMessageEventArgs e)
        {
            if (e.Sender == null || e.Message[0] != '!')
            {
                return;
            }

            var message = (string)e.Message;
            var messageArray = message.Split(' ');

            var command = RegisteredCommands.FirstOrDefault(cmd => cmd.Trigger.Equals(messageArray[0]));

            if (command == null)
            {
                return;
            }

            var input = message.Substring(messageArray[0].Length).Trim();

            var commandData = new CommandArguments
            {
                CommandType = ECommandType.IRC,
                SenderIdentity = e.Sender,
                Recipient = e.Recipient,
                Message = input
            };

            if (command.IsSteamCommand && !Steam.Instance.Client.IsConnected)
            {
                ReplyToCommand(commandData, "Not connected to Steam.");

                return;
            }
            else if (command.IsAdminCommand)
            {
                var ident = string.Format("{0}@{1}", e.Sender.Username, e.Sender.Hostname);

                if (!Settings.Current.IRC.Admins.Contains(ident))
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

            Log.WriteInfo("CommandHandler", "Handling IRC command {0} for user {1} in channel {2}", message, e.Sender, e.Recipient);

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

            HandleSteamMessage(callback.Sender, callback.Message, ECommandType.SteamIndividual);

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

            HandleSteamMessage(callback.ChatterID, callback.Message, ECommandType.SteamChatRoom, callback.ChatRoomID);

            Log.WriteInfo("CommandHandler", "Handling Steam command {0} for user {1} in chatroom {2}", callback.Message, callback.ChatterID, callback.ChatRoomID);
        }

        private void HandleSteamMessage(SteamID sender, string message, ECommandType commandType, SteamID chatRoom = null)
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
                CommandType = commandType,
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
