/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using SteamKit2;

namespace SteamDatabaseBackend
{
    internal class SteamIDCommand : Command
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
                command.Reply($"Usage:{Colors.OLIVE} steamid <steamid> [individual/group/gamegroup]");

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
                    command.Reply($"Failed to resolve vanity URL: {Colors.RED}{eResult}");

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
                var clantag = string.IsNullOrEmpty(callback.ClanTag) ?
                    string.Empty :
                    $" {Colors.NORMAL}(Clan tag: {Colors.LIGHTGRAY}{callback.ClanTag}{Colors.NORMAL})";

                command.Reply($"{Colors.BLUE}{callback.Name}{Colors.NORMAL} -{Colors.DARKBLUE} https://steamcommunity.com/gid/{callback.FriendID.ConvertToUInt64()}/{clantag}");
            }
            else if (callback.FriendID.IsIndividualAccount)
            {
                command.Reply($"{Colors.BLUE}{callback.Name}{Colors.NORMAL} -{Colors.DARKBLUE} https://steamcommunity.com/profiles/{callback.FriendID.ConvertToUInt64()}/");
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
            || steamID.SetFromSteam3String(input))
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
                        new Dictionary<string, object>
                        {
                            { "vanityurl", input },
                            { "url_type", (int)urlType }
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
            var displayInstance = input.AccountInstance switch
            {
                SteamID.AllInstances => "All",
                SteamID.DesktopInstance => "Desktop",
                SteamID.ConsoleInstance => "Console",
                SteamID.WebInstance => "Web",
                (uint)SteamID.ChatInstanceFlags.Clan => "Clan",
                (uint)SteamID.ChatInstanceFlags.Lobby => "Lobby",
                (uint)SteamID.ChatInstanceFlags.MMSLobby => "MMS Lobby",
                _ => input.AccountInstance.ToString()
            };

            return $"{input.Render()} / {input.ConvertToUInt64()} {Colors.DARKGRAY}(AccountID: {input.AccountID}, Universe: {input.AccountUniverse}, Instance: {displayInstance}, Type: {input.AccountType}){(input.IsValid ? "" : $"{Colors.RED} (not valid)")}";
        }

        /*
         * PersonaState message returns default jobid, so we have to work that around
         */
        private static JobID FakePersonaStateJob(SteamID steamID)
        {
            Steam.Instance.Friends.RequestFriendInfo(steamID,
                steamID.IsClanAccount ?
                    EClientPersonaStateFlag.PlayerName | EClientPersonaStateFlag.ClanData :
                    EClientPersonaStateFlag.PlayerName | EClientPersonaStateFlag.LastSeen
            );

            return new JobID(steamID);
        }
    }
}
