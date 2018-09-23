/*
 * Copyright (c) 2013-2018, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;

namespace SteamDatabaseBackend
{
    static class StoreQueue
    {
        public static async Task AddAppToQueue(uint appID)
        {
            await InsertQuery(new List<uint> { appID }, "app");
        }

        public static async Task AddAppToQueue(IEnumerable<uint> appIDs)
        {
            await InsertQuery(appIDs, "app");
        }

        public static async Task AddPackageToQueue(uint packageID)
        {
            await InsertQuery(new List<uint> { packageID }, "sub");
        }

        public static async Task AddPackageToQueue(IEnumerable<uint> packageIDs)
        {
            await InsertQuery(packageIDs, "sub");
        }

        private static async Task InsertQuery(IEnumerable<uint> ids, string type)
        {
            if (!Settings.Current.CanQueryStore)
            {
                return;
            }

            var items = ids.Select(x => new StoreUpdateQueue { ID = x, Type = type });

            using (var db = Database.Get())
            {
                await db.ExecuteAsync("INSERT INTO `StoreUpdateQueue` (`ID`, `Type`) VALUES (@ID, @Type) ON DUPLICATE KEY UPDATE `ID` = `ID`", items);
            }
        }
    }
}
