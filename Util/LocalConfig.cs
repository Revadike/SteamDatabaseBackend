/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
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
        class LocalConfigJson
        {
            [JsonProperty]
            public int CellID;

            [JsonProperty]
            public IPEndPoint[] ServerList;

            [JsonProperty]
            public string SentryFileName;

            [JsonProperty]
            public byte[] Sentry;
        }

        public static int CellID { get; set; }

        public static string SentryFileName { get; set; }

        public static byte[] Sentry { get; set; }

        public static void Load()
        {
            var path = GetPath();

            if (!File.Exists(path))
            {
                LoadServers();

                return;
            }

            var current = JsonConvert.DeserializeObject<LocalConfigJson>(File.ReadAllText(path), GetSettings());

            CellID = current.CellID;
            Sentry = current.Sentry;
            SentryFileName = current.SentryFileName;

            if (current.ServerList.Length > 0)
            {
                foreach (var endPoint in current.ServerList)
                {
                    CMClient.Servers.TryAdd(endPoint);
                }
            }
            else
            {
                LoadServers();
            }
        }

        public static void Save()
        {
            Log.WriteDebug("Local Config", "Saving...");

            var current = new LocalConfigJson
            {
                ServerList = CMClient.Servers.GetAllEndPoints(),
                CellID = CellID,
                Sentry = Sentry,
                SentryFileName = SentryFileName,
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
