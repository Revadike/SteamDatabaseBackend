/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MySql.Data.MySqlClient;
using Newtonsoft.Json;
using SteamKit2;

namespace SteamDatabaseBackend
{
    static class DbWorker
    {
        public static MySqlDataReader ExecuteReader(string text)
        {
            return MySqlHelper.ExecuteReader(Settings.Current.ConnectionString, text);
        }

        public static MySqlDataReader ExecuteReader(string text, params MySqlParameter[] parameters)
        {
            return MySqlHelper.ExecuteReader(Settings.Current.ConnectionString, text, parameters);
        }

        public static int ExecuteNonQuery(string text, params MySqlParameter[] parameters)
        {
            int res;

            try
            {
                res = MySqlHelper.ExecuteNonQuery(Settings.Current.ConnectionString, text, parameters);
            }
            catch (MySqlException e)
            {
                Log.WriteError("DbWorker", "Caught exception while executing a query: {0}\nMessage: {1}\n{2}", text, e.Message, e.StackTrace);

                // Try again
                res = MySqlHelper.ExecuteNonQuery(Settings.Current.ConnectionString, text, parameters);
            }

            return res;
        }

        public static string GetString(string fieldName, MySqlDataReader reader)
        {
            var ordinal = reader.GetOrdinal(fieldName);

            return reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
        }

        public static string JsonifyKeyValue(KeyValue keys)
        {
            string value = string.Empty;

            using (var sw = new StringWriter(new StringBuilder()))
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
