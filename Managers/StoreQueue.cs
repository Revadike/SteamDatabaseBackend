/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System.Collections.Generic;
using MySql.Data.MySqlClient;

namespace SteamDatabaseBackend
{
    public static class StoreQueue
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
            // Maintenance hell
            var values = string.Join(string.Format(", '{0}'), (", type), ids);

            values = string.Format("({0}, '{1}')", values.Remove(values.Length - 2), type);

            DbWorker.ExecuteNonQuery(string.Format("INSERT INTO `StoreUpdateQueue` (`ID`, `Type`) VALUES {0} ON DUPLICATE KEY UPDATE `ID` = `ID`", values));
        }
    }
}
