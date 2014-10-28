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
            return MySqlHelper.ExecuteNonQuery(Settings.Current.ConnectionString, text, parameters);
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
