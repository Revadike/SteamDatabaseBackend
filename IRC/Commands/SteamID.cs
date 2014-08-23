/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using SteamKit2;

namespace SteamDatabaseBackend
{
    class SteamIDCommand : Command
    {
        public SteamIDCommand()
        {
            Trigger = "!steamid";
            IsSteamCommand = true;

            Steam.Instance.CallbackManager.Register(new Callback<SteamFriends.PersonaStateCallback>(OnPersonaState));
        }

        public override void OnCommand(CommandArguments command)
        {
            if (command.Message.Length == 0)
            {
                CommandHandler.ReplyToCommand(command, "Usage:{0} !steamid <steamid>", Colors.OLIVE);

                return;
            }

            SteamID steamID;

            if (!TrySetSteamID(command.Message, out steamID))
            {
                CommandHandler.ReplyToCommand(command, "Invalid SteamID.");

                return;
            }

            CommandHandler.ReplyToCommand(command, ExpandSteamID(steamID));

            if (!steamID.IsIndividualAccount && !steamID.IsClanAccount)
            {
                return;
            }

            JobAction job;

            if (JobManager.TryRemoveJob(new JobID(steamID), out job) && job.IsCommand)
            {
                CommandHandler.ReplyToCommand(job.CommandRequest.Command, "Your request was lost in space.");
            }

            JobManager.AddJob(
                () => FakePersonaStateJob(steamID),
                new JobManager.IRCRequest
                {
                    Command = command
                }
            );
        }

        private static void OnPersonaState(SteamFriends.PersonaStateCallback callback)
        {
            JobAction job;

            if (!JobManager.TryRemoveJob(new JobID(callback.FriendID), out job) || !job.IsCommand)
            {
                return;
            }

            var command = job.CommandRequest.Command;

            if (callback.FriendID.IsClanAccount)
            {
                CommandHandler.ReplyToCommand(command, "{0} - https://steamcommunity.com/gid/{1}/ (Clan tag: {2})",
                    callback.Name, callback.FriendID.ConvertToUInt64(), callback.ClanTag);
            }
            else if (callback.FriendID.IsIndividualAccount)
            {
                CommandHandler.ReplyToCommand(command, "{0} - https://steamcommunity.com/profiles/{1}/ (Last login: {2}, Last logoff: {3})",
                    callback.Name, callback.FriendID.ConvertToUInt64(), callback.LastLogOn, callback.LastLogOff);
            }
            else
            {
                CommandHandler.ReplyToCommand(command, callback.Name);
            }
        }

        private static bool TrySetSteamID(string input, out SteamID steamID)
        {
            steamID = new SteamID();

            if (steamID.SetFromString(input, EUniverse.Public)
            ||  steamID.SetFromSteam3String(input))
            {
                return true;
            }

            ulong numericInput;

            if (ulong.TryParse(input, out numericInput))
            {
                steamID.SetFromUInt64(numericInput);

                return true;
            }

            return false;
        }

        /*
         * From VoiDeD's bot: https://github.com/VoiDeD/steam-irc-bot/blob/cedb7636e529fa226188ce102f5ee1337f8bed63/SteamIrcBot/Utils/Utils.cs#L191
         */
        private static string ExpandSteamID(SteamID input)
        {
            string displayInstance = input.AccountInstance.ToString();

            switch (input.AccountInstance)
            {
                case SteamID.AllInstances:
                    displayInstance = "All";
                    break;

                case SteamID.DesktopInstance:
                    displayInstance = "Desktop";
                    break;

                case SteamID.ConsoleInstance:
                    displayInstance = "Console";
                    break;

                case SteamID.WebInstance:
                    displayInstance = "Web";
                    break;

                case (uint)SteamID.ChatInstanceFlags.Clan:
                    displayInstance = "Clan";
                    break;

                case (uint)SteamID.ChatInstanceFlags.Lobby:
                    displayInstance = "Lobby";
                    break;

                case (uint)SteamID.ChatInstanceFlags.MMSLobby:
                    displayInstance = "MMS Lobby";
                    break;
            }

            return string.Format("{0} / {1} (UInt64: {2}, AccountID: {3}, IsValid: {4}, Universe: {5}, Instance: {6}, Type: {7})",
                input.Render(), input.Render(true), input.ConvertToUInt64(), input.AccountID, input.IsValid, input.AccountUniverse, displayInstance, input.AccountType);
        }

        /*
         * PersonaState message returns default jobid, so we have to work that around
         */
        private static JobID FakePersonaStateJob(SteamID steamID)
        {
            Steam.Instance.Friends.RequestFriendInfo(steamID,
                steamID.IsClanAccount ?
                    EClientPersonaStateFlag.PlayerName | EClientPersonaStateFlag.ClanTag :
                    EClientPersonaStateFlag.PlayerName | EClientPersonaStateFlag.LastSeen
            );

            return new JobID(steamID);
        }
    }
}
