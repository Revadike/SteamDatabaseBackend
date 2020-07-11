/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System.Threading.Tasks;
using Dapper;

namespace SteamDatabaseBackend
{
    internal class DepotCommand : Command
    {
        public DepotCommand()
        {
            Trigger = "depot";
        }

        public override async Task OnCommand(CommandArguments command)
        {
            if (command.Message.Length == 0 || !uint.TryParse(command.Message, out var depotId))
            {
                command.Reply($"Usage:{Colors.OLIVE} depot <depotid>");

                return;
            }

            await using var db = await Database.GetConnectionAsync();
            var depot = await db.QueryFirstOrDefaultAsync<Depot>("SELECT `DepotID`, `Name` FROM `Depots` WHERE `DepotID` = @depotId", new { depotId });

            if (depot == default)
            {
                command.Reply($"Unknown DepotID: {Colors.BLUE}{depotId}");

                return;
            }

            command.Reply($"{Colors.BLUE}{Utils.RemoveControlCharacters(depot.Name)}{Colors.NORMAL} -{Colors.DARKBLUE} {SteamDB.GetDepotUrl(depot.DepotID)}");
        }
    }
}
