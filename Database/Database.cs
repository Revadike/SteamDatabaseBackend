/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System.Threading.Tasks;
using MySqlConnector;

namespace SteamDatabaseBackend
{
    internal static class Database
    {
        public static async Task<MySqlConnection> GetConnectionAsync()
        {
            var connection = new MySqlConnection(Settings.Current.ConnectionString);

            await connection.OpenAsync();

            return connection;
        }

        public static MySqlConnection Get()
        {
            return new MySqlConnection(Settings.Current.ConnectionString);
        }
    }
}
