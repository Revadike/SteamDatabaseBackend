/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System.Linq;
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

        public override void OnCommand(CommandArguments command)
        {
            if (command.Message.Length == 0)
            {
                CommandHandler.ReplyToCommand(command, "Usage:{0} servers <filter> - See https://developer.valvesoftware.com/wiki/Master_Server_Query_Protocol", Colors.OLIVE);

                return;
            }

            if (command.Message.IndexOf('\\') == -1)
            {
                CommandHandler.ReplyToCommand(command, "That doesn't look like a filter.");

                return;
            }

            var request = new CGameServers_GetServerList_Request
            {
                filter = command.Message,
                limit = 5000,
            };

            JobManager.AddJob(
                () => GameServers.SendMessage(api => api.GetServerList(request)), 
                new JobManager.IRCRequest
                {
                    Type = JobManager.IRCRequestType.TYPE_GAMESERVERS,
                    Command = command
                }
            );
        }

        public static void OnServiceMethod(SteamUnifiedMessages.ServiceMethodResponse callback, JobManager.IRCRequest request)
        {
            var response = callback.GetDeserializedResponse<CGameServers_GetServerList_Response>();
            var servers = response.servers;

            if (!servers.Any())
            {
                CommandHandler.ReplyToCommand(request.Command, "No servers.");

                return;
            }

            if (servers.Count == 1)
            {
                var server = servers.First();

                CommandHandler.ReplyToCommand(request.Command, "{0} - {1} - {2}/{3} - Map: {4} - AppID: {5} - Version: {6} - Dir: {7} - Tags: {8} - Name: {9}",
                    server.addr, new SteamID(server.steamid).Render(true), server.players, server.max_players, server.map, server.appid, server.version, server.gamedir, server.gametype, server.name
                );

                return;
            }

            var serv = servers.Take(5).Select(x => string.Format("{0} ({1})", x.addr, x.players));

            CommandHandler.ReplyToCommand(request.Command, "{0}{1}", string.Join(", ", serv), servers.Count > 5 ? string.Format(", and {0}{1} more", servers.Count == 5000 ? ">" : "", servers.Count - 5) : "");
        }
    }
}
