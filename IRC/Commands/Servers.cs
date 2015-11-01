/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System.Linq;
using System.Threading.Tasks;
using SteamKit2;
using SteamKit2.Unified.Internal;

namespace SteamDatabaseBackend
{
    class ServersCommand : Command
    {
        private readonly SteamUnifiedMessages.UnifiedService<IGameServers> GameServers;

        public ServersCommand()
        {
            Trigger = "servers";
            IsSteamCommand = true;

            GameServers = Steam.Instance.Client.GetHandler<SteamUnifiedMessages>().CreateService<IGameServers>();
        }

        public override async Task OnCommand(CommandArguments command)
        {
            if (command.Message.Length == 0)
            {
                command.Reply("Usage:{0} servers <filter> - See https://developer.valvesoftware.com/wiki/Master_Server_Query_Protocol", Colors.OLIVE);

                return;
            }

            if (command.Message.IndexOf('\\') == -1)
            {
                command.Reply("That doesn't look like a filter.");

                return;
            }

            var request = new CGameServers_GetServerList_Request
            {
                filter = command.Message,
                limit = 5000,
            };

            var callback = await GameServers.SendMessage(api => api.GetServerList(request));
            var response = callback.GetDeserializedResponse<CGameServers_GetServerList_Response>();
            var servers = response.servers;

            if (!servers.Any())
            {
                command.Reply("No servers.");

                return;
            }

            if (servers.Count == 1)
            {
                var server = servers.First();

                command.Reply("{0} - {1} - {2}/{3} - Map: {4} - AppID: {5} - Version: {6} - Dir: {7} - Tags: {8} - Name: {9}",
                    server.addr, new SteamID(server.steamid).Render(true), server.players, server.max_players, server.map, server.appid, server.version, server.gamedir, server.gametype, server.name
                );

                return;
            }

            var serv = servers.Take(5).Select(x => string.Format("{0} ({1})", x.addr, x.players));

            command.Reply("{0}{1}", string.Join(", ", serv), servers.Count > 5 ? string.Format(", and {0}{1} more", servers.Count == 5000 ? ">" : "", servers.Count - 5) : "");
        }
    }
}
