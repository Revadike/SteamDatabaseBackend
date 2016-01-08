/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using Newtonsoft.Json;
using SteamKit2;
using SteamKit2.Internal;

namespace SteamDatabaseBackend
{
    static class LocalConfig
    {
        [JsonObject(MemberSerialization.OptIn)]
        public class CDNAuthToken
        {
            [JsonProperty]
            public string Server { get; set; }

            [JsonProperty]
            public string Token { get; set; }

            [JsonProperty]
            public DateTime Expiration { get; set; }
        }

        [JsonObject(MemberSerialization.OptIn)]
        class LocalConfigJson
        {
            [JsonProperty]
            public uint CellID { get; set; } 

            [JsonProperty]
            public string SentryFileName { get; set; } 

            [JsonProperty]
            public byte[] Sentry { get; set; } 

            [JsonProperty]
            public Dictionary<uint, CDNAuthToken> CDNAuthTokens { get; set; } 

            public LocalConfigJson()
            {
                CDNAuthTokens = new Dictionary<uint, CDNAuthToken>();
            }
        }

        public static uint CellID { get; set; }

        public static string SentryFileName { get; set; }

        public static byte[] Sentry { get; set; }

        public static Dictionary<uint, CDNAuthToken> CDNAuthTokens { get; private set; }

        public static void Load()
        {
            LoadServers();

            var path = GetPath();

            if (!File.Exists(path))
            {
                return;
            }

            var current = JsonConvert.DeserializeObject<LocalConfigJson>(File.ReadAllText(path), GetSettings());

            CellID = current.CellID;
            Sentry = current.Sentry;
            SentryFileName = current.SentryFileName;
            CDNAuthTokens = current.CDNAuthTokens;
        }

        public static void Save()
        {
            Log.WriteDebug("Local Config", "Saving...");

            var current = new LocalConfigJson
            {
                CellID = CellID,
                Sentry = Sentry,
                SentryFileName = SentryFileName,
                CDNAuthTokens = CDNAuthTokens,
            };

            var json = JsonConvert.SerializeObject(current, GetSettings());

            File.WriteAllText(GetPath(), json);
        }

        public static void LoadServers()
        {
            Log.WriteInfo("Steam", "Loading Steam servers...");

            var loadServersTask = SteamDirectory.Initialize(CellID);
            loadServersTask.Wait();

            if (loadServersTask.IsFaulted)
            {
                throw loadServersTask.Exception;
            }
        }

        private static JsonSerializerSettings GetSettings()
        {
            var settings = new JsonSerializerSettings();
            settings.Converters.Add(new JsonConverters.IPAddressConverter());
            settings.Converters.Add(new JsonConverters.IPEndPointConverter());
            settings.Formatting = Formatting.Indented;

            return settings;
        }

        private static string GetPath()
        {
            return Path.Combine(Application.Path, "files", ".support", "localconfig.json");
        }
    }
}
