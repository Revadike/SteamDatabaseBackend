/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using MySql.Data.MySqlClient;

namespace SteamDatabaseBackend
{
    // TODO: Remove these functions and replace calls with Dapper ones
    static class DbWorker
    {
        public static MySqlDataReader ExecuteReader(string text, params MySqlParameter[] parameters)
        {
            return MySqlHelper.ExecuteReader(Settings.Current.ConnectionString, text, parameters);
        }

        public static int ExecuteNonQuery(string text, params MySqlParameter[] parameters)
        {
            return MySqlHelper.ExecuteNonQuery(Settings.Current.ConnectionString, text, parameters);
        }
    }
}
