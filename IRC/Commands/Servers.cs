/*
 * Copyright (c) 2013-2018, SteamDB. All rights reserved.
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

            GameServers = Steam.Instance.UnifiedMessages.CreateService<IGameServers>();
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
                limit = int.MaxValue,
            };

            var callback = await GameServers.SendMessage(api => api.GetServerList(request));
            var response = callback.GetDeserializedResponse<CGameServers_GetServerList_Response>();
            var servers = response.servers;

            if (servers.Count == 0)
            {
                command.Reply("No servers.");

                return;
            }

            if (servers.Count == 1)
            {
                var server = servers[0];

                command.Reply($"{server.addr} - {new SteamID(server.steamid).Render(true)} - {Colors.GREEN}{server.players}/{server.max_players}{Colors.NORMAL} - Map: {Colors.DARKGRAY}{server.map}{Colors.NORMAL} - AppID: {Colors.DARKGRAY}{server.appid}{Colors.NORMAL} - Version: {Colors.DARKGRAY}{server.version}{Colors.NORMAL} - Dir: {Colors.DARKGRAY}{server.gamedir}{Colors.NORMAL} - Tags: {Colors.DARKGRAY}{server.gametype}{Colors.NORMAL} - Name: {Colors.DARKGRAY}{server.name}");

                return;
            }

            command.Reply($"{Colors.GREEN}{servers.Sum(x => x.players)}{Colors.NORMAL} players on {Colors.GREEN}{servers.Count}{Colors.NORMAL} servers. First three: {string.Join(" / ", servers.Take(3).Select(x => x.addr))}");
        }
    }
}
