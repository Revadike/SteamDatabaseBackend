/*
 * Copyright (c) 2013, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Text;
using MySql.Data;
using MySql.Data.MySqlClient;
using System.Linq;

namespace PICSUpdater
{
    public static class DbWorker
    {
        private static string ConnectionString = ConfigurationManager.AppSettings["mysql-cstring"];

        public static MySqlDataReader ExecuteReader(string text)
        {
            return MySqlHelper.ExecuteReader(ConnectionString, text);
        }

        public static MySqlDataReader ExecuteReader(string text, MySqlParameter[] parameters)
        {
            return MySqlHelper.ExecuteReader(ConnectionString, text, parameters);
        }

        public static int ExecuteNonQuery(string text)
        {
            return ExecuteNonQuery(text, null);
        }

        public static int ExecuteNonQuery(string text, MySqlParameter[] parameters)
        {
            return MySqlHelper.ExecuteNonQuery(ConnectionString, text, parameters);
        }

        public static string GetString(string SqlFieldName, MySqlDataReader Reader)
        {
            var ordinal = Reader.GetOrdinal(SqlFieldName);

            return Reader.IsDBNull(ordinal) ? String.Empty : Reader.GetString(ordinal);
        }
    }
}
