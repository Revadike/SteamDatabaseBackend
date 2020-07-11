/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NetIrc2.Events;
using SteamKit2;

namespace SteamDatabaseBackend
{
    internal class CommandHandler
    {
        private readonly List<Command> RegisteredCommands;
        private readonly Regex DiscordRelayMessageRegex;

        public CommandHandler()
        {
            RegisteredCommands = new List<Command>
            {
                new BlogCommand(),
                new PlayersCommand(),
                new AppCommand(),
                new PackageCommand(),
                new DepotCommand(),
                new SteamIDCommand(),
                new GIDCommand(),
                new PubFileCommand(),
                new UGCCommand(),
                new EnumCommand(),
                new ServersCommand(),
                new BinariesCommand(),
                new ImportantCommand(),
                new ReloginCommand(),
            };

            if (Settings.Current.CanQueryStore)
            {
                RegisteredCommands.Add(new QueueCommand());
                RegisteredCommands.Add(new KeyCommand());
            }

            // Register help command last so we can pass the list of the commands
            RegisteredCommands.Add(new HelpCommand(RegisteredCommands));

            DiscordRelayMessageRegex = new Regex(
                "^<(?<name>.+?)\x03> (?<message>.+)$",
                RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture
            );

            Log.WriteInfo("CommandHandler", "Registered {0} commands", RegisteredCommands.Count);
        }

        public void OnIRCMessage(object sender, ChatMessageEventArgs e)
        {
            var commandData = new CommandArguments
            {
                CommandType = ECommandType.IRC,
                SenderIdentity = e.Sender,
                Nickname = e.Sender.Nickname.ToString(),
                Recipient = e.Recipient,
                Message = e.Message
            };

            if (commandData.SenderIdentity.Hostname == "steamdb/discord-relay")
            {
                var match = DiscordRelayMessageRegex.Match(commandData.Message);

                if (!match.Success)
                {
                    return;
                }

                // Remove IRC colors, remove control characters, remove zero width space, add @ and a space
                commandData.Nickname = $"@{Utils.RemoveControlCharacters(Colors.StripColors(match.Groups["name"].Value.Replace("\u200B", "")))} ";
                commandData.Message = match.Groups["message"].Value;
            }

            if (commandData.Message[0] != Settings.Current.IRC.CommandPrefix)
            {
                return;
            }

            var message = commandData.Message;
            var messageArray = message.Split(' ');
            var trigger = messageArray[0];

            if (trigger.Length < 2)
            {
                return;
            }

            trigger = trigger.Substring(1);

            var command = RegisteredCommands.Find(cmd => cmd.Trigger == trigger);

            if (command == null)
            {
                return;
            }

            commandData.Message = message.Substring(messageArray[0].Length).Trim();

            if (command.IsSteamCommand && !Steam.Instance.Client.IsConnected)
            {
                commandData.Reply("Not connected to Steam.");

                return;
            }

            var ident = $"{e.Sender.Username}@{e.Sender.Hostname}";
            commandData.IsUserAdmin = Settings.Current.IRC.Admins.Contains(ident);

            if (command.IsAdminCommand && !commandData.IsUserAdmin)
            {
                return;
            }

            Log.WriteInfo("CommandHandler", "Handling IRC command \"{0}\" for {1}", Utils.RemoveControlCharacters(Colors.StripColors(message)), commandData);

            TryCommand(command, commandData);
        }

        public void OnSteamFriendMessage(SteamFriends.FriendMsgCallback callback)
        {
            if (callback.EntryType != EChatEntryType.ChatMsg        // Is chat message
            || callback.Sender == Steam.Instance.Client.SteamID    // Is not sent by the bot
            || callback.Message[0] != Settings.Current.IRC.CommandPrefix
            || callback.Message.Contains('\n')                     // Does not contain new lines
            )
            {
                return;
            }

            var commandData = new CommandArguments
            {
                CommandType = ECommandType.SteamIndividual,
                SenderID = callback.Sender,
                Message = callback.Message
            };

            Log.WriteInfo("CommandHandler", "Handling Steam friend command \"{0}\" for {1}", callback.Message, commandData);

            HandleSteamMessage(commandData);
        }

        private void HandleSteamMessage(CommandArguments commandData)
        {
            var message = commandData.Message;
            var i = message.IndexOf(' ');
            var inputCommand = i == -1 ? message.Substring(1) : message[1..i];

            var command = RegisteredCommands.Find(cmd => cmd.Trigger == inputCommand);

            if (command == null)
            {
                return;
            }

            commandData.Message = i == -1 ? string.Empty : message.Substring(i).Trim();
            commandData.IsUserAdmin = Settings.Current.SteamAdmins.Contains(commandData.SenderID.ConvertToUInt64());

            if (command.IsAdminCommand && !commandData.IsUserAdmin)
            {
                commandData.Reply("You're not an admin!");

                return;
            }

            TryCommand(command, commandData);
        }

        private static async void TryCommand(Command command, CommandArguments commandData)
        {
            try
            {
                await command.OnCommand(commandData);
            }
            catch (TaskCanceledException)
            {
                commandData.Reply($"Your {Colors.OLIVE}{command.Trigger}{Colors.NORMAL} command timed out.");
            }
            catch (AsyncJobFailedException)
            {
                commandData.Reply($"Steam says this job failed. Unable to execute your {Colors.OLIVE}{command.Trigger}{Colors.NORMAL} command.");
            }
            catch (Exception e)
            {
                ErrorReporter.Notify("IRC", e);

                commandData.Reply($"Exception: {e.Message}");
            }
        }
    }
}
