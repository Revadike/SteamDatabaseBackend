/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace SteamDatabaseBackend
{
    internal static class LocalConfig
    {
        private static readonly JsonSerializerSettings JsonFormatted = new JsonSerializerSettings { Formatting = Formatting.Indented };

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
            public string LoginKey { get; set; }

            [JsonProperty]
            public Dictionary<uint, uint> FreeLicensesToRequest { get; set; }

            public LocalConfigJson()
            {
                FreeLicensesToRequest = new Dictionary<uint, uint>();
            }
        }

        private static readonly string ConfigPath = Path.Combine(Application.Path, "files", ".support", "localconfig.json");
        private static readonly object saveLock = new object();

        public static LocalConfigJson Current { get; private set; } = new LocalConfigJson();

        public static async Task Load()
        {
            if (File.Exists(ConfigPath))
            {
                Current = JsonConvert.DeserializeObject<LocalConfigJson>(await File.ReadAllTextAsync(ConfigPath));
            }

            if (Current.FreeLicensesToRequest.Count > 0)
            {
                Log.WriteInfo("Local Config", $"There are {Current.FreeLicensesToRequest.Count} free licenses to request");
            }

            Save();
        }

        public static void Save()
        {
            Log.WriteDebug("Local Config", "Saving...");

            lock (saveLock)
            {
                var data = JsonConvert.SerializeObject(Current, JsonFormatted);

                File.WriteAllText(ConfigPath, data);
            }
        }
    }
}
