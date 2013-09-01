/*
 * Copyright (c) 2013, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Text;
using MySql.Data.MySqlClient;
using Newtonsoft.Json;
using SteamKit2;

namespace PICSUpdater
{
    public static class DbWorker
    {
        private static string ConnectionString = ConfigurationManager.AppSettings["mysql-cstring"];

        public static MySqlDataReader ExecuteReader(string text)
        {
            return MySqlHelper.ExecuteReader(ConnectionString, text);
        }

        public static MySqlDataReader ExecuteReader(string text, params MySqlParameter[] parameters)
        {
            return MySqlHelper.ExecuteReader(ConnectionString, text, parameters);
        }

        public static int ExecuteNonQuery(string text, params MySqlParameter[] parameters)
        {
            return MySqlHelper.ExecuteNonQuery(ConnectionString, text, parameters);
        }

        public static string GetString(string SqlFieldName, MySqlDataReader Reader)
        {
            var ordinal = Reader.GetOrdinal(SqlFieldName);

            return Reader.IsDBNull(ordinal) ? String.Empty : Reader.GetString(ordinal);
        }

        public static string JsonifyKeyValue(KeyValue keys)
        {
            string value = "";

            using(StringWriter sw = new StringWriter(new StringBuilder()))
            {
                using (JsonWriter w = new JsonTextWriter(sw))
                {
                    DbWorker.JsonifyKeyValue(w, keys.Children);
                }

                value = sw.ToString();
            }

            return value;
        }

        private static void JsonifyKeyValue(JsonWriter w, List<KeyValue> keys)
        {
            w.WriteStartObject();

            foreach (KeyValue keyval in keys)
            {
                if (keyval.Children.Count > 0)
                {
                    w.WritePropertyName(keyval.Name);
                    JsonifyKeyValue(w, keyval.Children);
                }
                else if (keyval.Value != null) // TODO: Should we be writing null keys anyway?
                {
                    w.WritePropertyName(keyval.Name);
                    w.WriteValue(keyval.Value);
                }
            }

            w.WriteEndObject();
        }
    }
}
