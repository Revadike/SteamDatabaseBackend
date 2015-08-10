/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System.Collections.Generic;
using System.Linq;
using Dapper;

namespace SteamDatabaseBackend
{
    static class StoreQueue
    {
        public static void AddAppToQueue(uint appID)
        {
            InsertQuery(new List<uint> { appID }, "app");
        }

        public static void AddAppToQueue(IEnumerable<uint> appIDs)
        {
            InsertQuery(appIDs, "app");
        }

        public static void AddPackageToQueue(uint packageID)
        {
            InsertQuery(new List<uint> { packageID }, "sub");
        }

        public static void AddPackageToQueue(IEnumerable<uint> packageIDs)
        {
            InsertQuery(packageIDs, "sub");
        }

        private static void InsertQuery(IEnumerable<uint> ids, string type)
        {
            var items = ids.Select(x => new StoreUpdateQueue { ID = x, Type = type });

            using (var db = Database.GetConnection())
            {
                db.Execute("INSERT INTO `StoreUpdateQueue` (`ID`, `Type`) VALUES (@ID, @Type) ON DUPLICATE KEY UPDATE `ID` = `ID`", items);
            }
        }
    }
}
