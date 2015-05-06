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
        private PubFileCommand PubFileHandler;

        private static DateTime LastCommandUseTime = DateTime.Now;
        private static uint LastCommandUseCount = 0;

        public CommandHandler()
        {
            PubFileHandler = new PubFileCommand();

            RegisteredCommands = new List<Command>
            {
                new BlogCommand(),
                new PlayersCommand(),
                new AppCommand(),
                new PackageCommand(),
                new SteamIDCommand(),
                PubFileHandler,
                new UGCCommand(),
                new EnumCommand(),
                new BinariesCommand(),
                new ImportantCommand(),
                new ReloginCommand(),
            };

            // Register help command last so we can pass the list of the commands
            RegisteredCommands.Add(new HelpCommand(RegisteredCommands));

            Log.WriteInfo("CommandHandler", "Registered {0} commands", RegisteredCommands.Count);
        }

        public static void ReplyToCommand(CommandArguments command, bool notice, string message, params object[] args)
        {
            ReplyToCommand(command, string.Format(message, args), notice);
        }

        public static void ReplyToCommand(CommandArguments command, string message, params object[] args)
        {
            ReplyToCommand(command, string.Format(message, args), false);
        }

        private static void ReplyToCommand(CommandArguments command, string message, bool notice)
        {
            switch (command.CommandType)
            {
                case ECommandType.IRC:
                {
                    var isChannelMessage = IRC.IsRecipientChannel(command.Recipient);
                    bool shouldReplyAsNotice = false;
                    string recipient = command.Recipient;

                    if (isChannelMessage)
                    {
                        shouldReplyAsNotice = notice || command.ReplyAsNotice;

                        if (!shouldReplyAsNotice)
                        {
                            message = string.Format("{0}{1}{2}: {3}", Colors.LIGHTGRAY, command.SenderIdentity.Nickname, Colors.NORMAL, message);
                        }
                        else
                        {
                            recipient = command.SenderIdentity.Nickname.ToString();
                        }
                    }
                    else
                    {
                        recipient = command.SenderIdentity.Nickname.ToString();
                    }

                    IRC.Instance.SendReply(recipient, message, shouldReplyAsNotice);

                    break;
                }

                case ECommandType.SteamChatRoom:
                {
                    if (!Steam.Instance.Client.IsConnected)
                    {
                        break;
                    }

                    Steam.Instance.Friends.SendChatRoomMessage(command.ChatRoomID, EChatEntryType.ChatMsg, string.Format("{0}: {1}", Steam.Instance.Friends.GetFriendPersonaName(command.SenderID), Colors.StripColors(message)));
                    
                    break;
                }

                case ECommandType.SteamIndividual:
                {
                    if (!Steam.Instance.Client.IsConnected)
                    {
                        break;
                    }

                    Steam.Instance.Friends.SendChatMessage(command.SenderID, EChatEntryType.ChatMsg, Colors.StripColors(message));
                    
                    break;
                }
            }
        }

        public void OnIRCMessage(object sender, ChatMessageEventArgs e)
        {
            PubFileHandler.OnMessage(e);

            if (e.Message[0] != '!')
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

            if (command.IsAdminCommand && !Settings.Current.SteamAdmins.Contains(sender.ConvertToUInt64()))
            {
                ReplyToCommand(commandData, "You're not an admin!");

                return;
            }

            TryCommand(command, commandData);
        }

        private static void TryCommand(Command command, CommandArguments commandData)
        {
            if (commandData.CommandType == ECommandType.IRC && IRC.IsRecipientChannel(commandData.Recipient))
            {
                if (DateTime.Now.Subtract(LastCommandUseTime).TotalSeconds < 60)
                {
                    commandData.ReplyAsNotice = ++LastCommandUseCount > 3;
                }
                else
                {
                    LastCommandUseCount = 1;
                }

                LastCommandUseTime = DateTime.Now;
            }

            try
            {
                command.OnCommand(commandData);
            }
            catch (Exception e)
            {
                Log.WriteError("CommandHandler", "Exception while executing a command: {0}\n{1}", e.Message, e.StackTrace);

                ReplyToCommand(commandData, "Exception: {0}", e.Message);

                ErrorReporter.Notify(e);
            }
        }
    }
}
