/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using MySql.Data.MySqlClient;

namespace SteamDatabaseBackend
{
    static class Database
    {
        public static MySqlConnection GetConnection()
        {
            var connection = new MySqlConnection(Settings.Current.ConnectionString);

            connection.Open();

            return connection;
        }
    }
}
