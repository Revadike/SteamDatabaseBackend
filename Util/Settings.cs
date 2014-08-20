/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.IO;
using Meebey.SmartIrc4net;
using MySql.Data.MySqlClient;
using Newtonsoft.Json;

namespace SteamDatabaseBackend
{
    static class Settings
    {
        private static SettingsJson _current = new SettingsJson();

        public static bool IsFullRun { get; private set; }

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

            using (var connection = new MySqlConnection(Settings.Current.ConnectionString))
            {
                connection.Open(); // Exception will be caught by whatever called Load()
            }

            if (Current.FullRun > 0)
            {
                IsFullRun = true;

                Log.WriteInfo("Settings", "Running full update with option \"{0}\"", Current.FullRun);

                // Don't log full runs, regardless of setting
                Current.LogToFile = false;

                // Don't connect to IRC while doing a full run
                Current.IRC.Enabled = false;
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

            if (!Rfc2812.IsValidNickname(Current.IRC.Nickname))
            {
                Log.WriteError("Settings", "Invalid IRC nickname");
                return false;
            }

            return true;
        }
    }
}
