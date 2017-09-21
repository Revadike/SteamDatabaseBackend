/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Newtonsoft.Json;

namespace SteamDatabaseBackend
{
    static class LocalConfig
    {
        private static readonly JsonSerializerSettings JsonFormatted = new JsonSerializerSettings { Formatting = Formatting.Indented };

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
        public class LocalConfigJson
        {
            [JsonProperty]
            public uint CellID { get; set; }

            [JsonProperty]
            public uint ChangeNumber { get; set; }

            [JsonProperty]
            public string SentryFileName { get; set; } 

            [JsonProperty]
            public byte[] Sentry { get; set; } 

            [JsonProperty]
            public ConcurrentDictionary<uint, CDNAuthToken> CDNAuthTokens { get; set; } 

            public LocalConfigJson()
            {
                CDNAuthTokens = new ConcurrentDictionary<uint, CDNAuthToken>();
            }
        }

        public static LocalConfigJson Current { get; private set; } = new LocalConfigJson();
        public static uint CellID => Current.CellID;
        public static byte[] Sentry => Current.Sentry;
        public static ConcurrentDictionary<uint, CDNAuthToken> CDNAuthTokens => Current.CDNAuthTokens;

        public static void Load()
        {
            var path = GetPath();

            if (File.Exists(path))
            {
                Current = JsonConvert.DeserializeObject<LocalConfigJson>(File.ReadAllText(path));
            }
            else
            {
                Save();
            }
        }

        public static void Save()
        {
            Log.WriteDebug("Local Config", "Saving...");
            
            File.WriteAllText(GetPath(), JsonConvert.SerializeObject(Current, JsonFormatted));
        }

        private static string GetPath()
        {
            return Path.Combine(Application.Path, "files", ".support", "localconfig.json");
        }
    }
}
