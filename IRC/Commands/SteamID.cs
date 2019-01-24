/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
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
            Trigger = "steamid";
            IsSteamCommand = true;

            Steam.Instance.CallbackManager.Subscribe<SteamFriends.PersonaStateCallback>(OnPersonaState);
        }

        public override async Task OnCommand(CommandArguments command)
        {
            await Task.Yield();

            if (command.Message.Length == 0)
            {
                command.Reply("Usage:{0} steamid <steamid> [individual/group/gamegroup]", Colors.OLIVE);

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
                        command.Reply("Invalid vanity URL type.");
                        return;
                }
            }

            if (urlType != EVanityURLType.Default || !TrySetSteamID(args[0], out var steamID))
            {
                if (urlType == EVanityURLType.Default)
                {
                    urlType = EVanityURLType.Individual;
                }

                EResult eResult;

                (eResult, steamID) = await ResolveVanityURL(args[0], urlType);

                if (eResult != EResult.OK)
                {
                    command.Reply("Failed to resolve vanity URL: {0}{1}", Colors.RED, eResult.ToString());

                    return;
                }
            }

            command.Reply(ExpandSteamID(steamID));

            if (!steamID.IsValid || (!steamID.IsIndividualAccount && !steamID.IsClanAccount))
            {
                return;
            }

            JobManager.TryRemoveJob(new JobID(steamID)); // Remove previous "job" if any

            JobManager.AddJob(
                () => FakePersonaStateJob(steamID),
                command
            );
        }

        private static void OnPersonaState(SteamFriends.PersonaStateCallback callback)
        {
            if (!JobManager.TryRemoveJob(new JobID(callback.FriendID), out var job))
            {
                return;
            }

            var command = job.Command;

            if (callback.FriendID.IsClanAccount)
            {
                var clantag = string.IsNullOrEmpty(callback.ClanTag) ? string.Empty : string.Format(" {0}(Clan tag: {1}{2}{3})",
                    Colors.NORMAL, Colors.LIGHTGRAY, callback.ClanTag, Colors.NORMAL);

                command.Reply("{0}{1}{2} -{3} https://steamcommunity.com/gid/{4}/{5}",
                    Colors.BLUE, callback.Name, Colors.NORMAL,
                    Colors.DARKBLUE, callback.FriendID.ConvertToUInt64(), clantag
                );
            }
            else if (callback.FriendID.IsIndividualAccount)
            {
                command.Reply("{0}{1}{2} -{3} https://steamcommunity.com/profiles/{4}/ {5}(Last login: {6}, Last logoff: {7})",
                    Colors.BLUE, callback.Name, Colors.NORMAL,
                    Colors.DARKBLUE, callback.FriendID.ConvertToUInt64(), Colors.DARKGRAY,
                    callback.LastLogOn.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    callback.LastLogOff.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss")
                );
            }
            else
            {
                command.Reply(callback.Name);
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

            if (ulong.TryParse(input, out var numericInput))
            {
                steamID.SetFromUInt64(numericInput);

                return true;
            }

            return false;
        }

        private static async Task<(EResult result, SteamID steamID)> ResolveVanityURL(string input, EVanityURLType urlType)
        {
            var steamID = new SteamID();
            EResult eResult;

            using (var steamUser = Steam.Configuration.GetAsyncWebAPIInterface("ISteamUser"))
            {
                steamUser.Timeout = TimeSpan.FromSeconds(5);

                KeyValue response;

                try
                {
                    response = await steamUser.CallAsync(HttpMethod.Get, "ResolveVanityURL", 1,
                        new Dictionary<string, string>
                        {
                            { "vanityurl", input },
                            { "url_type", ((int)urlType).ToString() }
                        });
                }
                catch (HttpRequestException)
                {
                    return (EResult.Timeout, steamID);
                }

                eResult = (EResult)response["success"].AsInteger();

                if (eResult == EResult.OK)
                {
                    steamID.SetFromUInt64((ulong)response["steamid"].AsLong());
                }
            }

            return (eResult, steamID);
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
                input.Render(true), input.Render(false), Colors.DARKGRAY, input.ConvertToUInt64(), input.AccountID, input.IsValid, input.AccountUniverse, displayInstance, input.AccountType);
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
