/*
 * Copyright (c) 2013, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections.Generic;
using System.IO;
using MySql.Data.MySqlClient;
using Newtonsoft.Json;
using SteamKit2;

namespace SteamDatabaseBackend
{
    public static class Settings
    {
        public sealed class SettingsJson
        {
            public sealed class SteamJson
            {
                public uint IdleAppID;
                public string Username;
                public string Password;
            }

            public sealed class SteamGCIdler
            {
                public uint AppID;
                public string Username;
                public string Password;
            }

            public sealed class IrcJson
            {
                public bool Enabled;
                public string[] Servers;
                public int Port;
                public string Nickname;
                public IrcChannelsJson Channel;
            }

            public sealed class IrcChannelsJson
            {
                public string Main;
                public string Announce;
            }

            public List<SteamID> ChatRooms;
            public Dictionary<uint, List<string>> ImportantFiles;
            public SteamGCIdler[] GameCoordinatorIdlers;
            public SteamJson Steam;
            public IrcJson IRC;

            public Uri BaseURL;
            public Uri RawBaseURL;
            public string ConnectionString;
            public uint FullRun;
            public bool SteamKitDebug;
            public bool LogToFile;
        }

        private static SettingsJson _current = new SettingsJson();

        public static SettingsJson Current
        {
            get
            {
                return _current;
            }
        }

        public static void Load()
        {
            string settingsFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");

            if (!File.Exists(settingsFile))
            {
                throw new FileNotFoundException("Settings file not found, must be in settings.json");
            }

            _current = JsonConvert.DeserializeObject<SettingsJson>(File.ReadAllText(settingsFile), new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error });

            if (string.IsNullOrWhiteSpace(Current.Steam.Username) || string.IsNullOrWhiteSpace(Current.Steam.Password))
            {
                throw new InvalidDataException("Missing Steam credentials in settings file");
            }

            using (MySqlConnection connection = new MySqlConnection(Settings.Current.ConnectionString))
            {
                connection.Open(); // Exception will be caught by whatever called Load()
            }

            if (Current.FullRun > 0)
            {
                Log.WriteInfo("Settings", "Running full update with option \"{0}\"", Settings.Current.FullRun);

                // Don't log full runs, regardless of setting
                Current.LogToFile = false;
            }
            else if (!Current.LogToFile)
            {
                Log.WriteInfo("Settings", "File logging is disabled");
            }
        }

        public static bool CanConnectToIRC()
        {
            if (!Current.IRC.Enabled)
            {
                Log.WriteWarn("Settings", "IRC is disabled in settings");
                return false;
            }

            if (Current.IRC.Servers.Length == 0 || Current.IRC.Port <= 0)
            {
                Log.WriteWarn("Settings", "Missing IRC details in settings file, not connecting");
                return false;
            }

            if (string.IsNullOrWhiteSpace(Current.IRC.Nickname))
            {
                Log.WriteError("Settings", "Missing IRC nickname in settings file, not connecting");
                return false;
            }

            return true;
        }
    }
}
