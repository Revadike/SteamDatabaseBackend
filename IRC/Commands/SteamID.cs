/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Net;
using SteamKit2;

namespace SteamDatabaseBackend
{
    class SteamIDCommand : Command
    {
        private enum EVanityURLType
        {
            Default,
            Individual,
            Group,
            OfficialGameGroup
        }

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
                CommandHandler.ReplyToCommand(command, "Usage:{0} !steamid <steamid> [individual/group/gamegroup]", Colors.OLIVE);

                return;
            }

            var args = command.Message.Split(' ');
            var urlType = EVanityURLType.Default;

            if (args.Length > 1)
            {
                switch (args[1])
                {
                    case "individual":
                        urlType = EVanityURLType.Individual;
                        break;

                    case "group":
                        urlType = EVanityURLType.Group;
                        break;

                    case "game":
                    case "gamegroup":
                        urlType = EVanityURLType.OfficialGameGroup;
                        break;

                    default:
                        CommandHandler.ReplyToCommand(command, "Invalid vanity url type.");
                        return;
                }
            }

            SteamID steamID;

            if (urlType != EVanityURLType.Default || !TrySetSteamID(args[0], out steamID))
            {
                if (urlType == EVanityURLType.Default)
                {
                    urlType = EVanityURLType.Individual;
                }

                var eResult = ResolveVanityURL(args[0], urlType, out steamID);

                if (eResult != EResult.OK)
                {
                    CommandHandler.ReplyToCommand(command, "Failed to resolve vanity url: {0}{1}", Colors.RED, eResult.ToString());

                    return;
                }
            }

            CommandHandler.ReplyToCommand(command, ExpandSteamID(steamID));

            if (!steamID.IsValid || (!steamID.IsIndividualAccount && !steamID.IsClanAccount))
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
                var clantag = string.IsNullOrEmpty(callback.ClanTag) ? string.Empty : string.Format(" {0}(Clan tag: {1}{2}{3})",
                    Colors.NORMAL, Colors.LIGHTGRAY, callback.ClanTag, Colors.NORMAL);

                CommandHandler.ReplyToCommand(command, "{0}{1}{2} -{3} https://steamcommunity.com/gid/{4}/{5}",
                    Colors.BLUE, callback.Name, Colors.NORMAL,
                    Colors.DARKBLUE, callback.FriendID.ConvertToUInt64(), clantag
                );
            }
            else if (callback.FriendID.IsIndividualAccount)
            {
                CommandHandler.ReplyToCommand(command, "{0}{1}{2} -{3} https://steamcommunity.com/profiles/{4}/ {5}(Last login: {6}, Last logoff: {7})",
                    Colors.BLUE, callback.Name, Colors.NORMAL,
                    Colors.DARKBLUE, callback.FriendID.ConvertToUInt64(),
                    Colors.DARKGRAY, callback.LastLogOn, callback.LastLogOff
                );
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

        private static EResult ResolveVanityURL(string input, EVanityURLType urlType, out SteamID steamID)
        {
            steamID = new SteamID();

            using (dynamic steamUser = WebAPI.GetInterface("ISteamUser", Settings.Current.Steam.WebAPIKey))
            {
                steamUser.Timeout = (int)TimeSpan.FromSeconds(5).TotalMilliseconds;

                KeyValue response;

                try
                {
                    response = steamUser.ResolveVanityURL(vanityurl: input, url_type: (int)urlType);
                }
                catch (WebException)
                {
                    return EResult.Timeout;
                }

                var eResult = (EResult)response["success"].AsInteger();

                if (eResult == EResult.OK)
                {
                    steamID.SetFromUInt64((ulong)response["steamid"].AsLong());
                }

                return eResult;
            }
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

            return string.Format("{0} / {1} {2}(UInt64: {3}, AccountID: {4}, IsValid: {5}, Universe: {6}, Instance: {7}, Type: {8})",
                input.Render(), input.Render(true), Colors.DARKGRAY, input.ConvertToUInt64(), input.AccountID, input.IsValid, input.AccountUniverse, displayInstance, input.AccountType);
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
