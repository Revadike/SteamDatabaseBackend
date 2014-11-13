/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using MySql.Data.MySqlClient;

namespace SteamDatabaseBackend
{
    public static class StoreQueue
    {
        public static void AddAppToQueue(uint appID)
        {
            DbWorker.ExecuteNonQuery("INSERT INTO `StoreUpdateQueue` (`ID`, `Type`) VALUES (@AppID, 'app') ON DUPLICATE KEY UPDATE `ID` = `ID`", new MySqlParameter("@AppID", appID));
        }

        public static void AddPackageToQueue(uint packageID)
        {
            DbWorker.ExecuteNonQuery("INSERT INTO `StoreUpdateQueue` (`ID`, `Type`) VALUES (@SubID, 'sub') ON DUPLICATE KEY UPDATE `ID` = `ID`", new MySqlParameter("@SubID", packageID));
        }
    }
}
