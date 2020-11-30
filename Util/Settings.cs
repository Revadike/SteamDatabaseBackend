/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System.IO;
using System.Threading.Tasks;
using Dapper;
using Newtonsoft.Json;

namespace SteamDatabaseBackend
{
    internal static class Settings
    {
        public static SettingsJson Current { get; private set; } = new SettingsJson();

        public static FullRunState FullRun { get; set; }

        public static bool IsFullRun => FullRun != FullRunState.None;

        public static bool IsMillhaven { get; private set; }

        public static async Task Load()
        {
            var settingsFile = Path.Combine(Application.Path, "settings.json");

            if (!File.Exists(settingsFile))
            {
                throw new FileNotFoundException($"\"{settingsFile}\" does not exist. Rename and edit settings.json.default file.");
            }

            Current = JsonConvert.DeserializeObject<SettingsJson>(await File.ReadAllTextAsync(settingsFile), new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error }) ?? new SettingsJson();

            if (FullRun != FullRunState.None)
            {
                // Don't log full runs, regardless of setting
                Current.LogToFile = false;
            }
        }

        public static async Task Initialize()
        {
            if (string.IsNullOrWhiteSpace(Current.Steam.Username) || string.IsNullOrWhiteSpace(Current.Steam.Password))
            {
                throw new InvalidDataException("Missing Steam credentials in settings file");
            }

            // Test database connection, it will throw if connection is unable to be made
            await using (var db = await Database.GetConnectionAsync())
            {
                IsMillhaven = await db.ExecuteScalarAsync<string>("SHOW TABLES LIKE 'Store'") != null;
            }
            
            if (!Current.LogToFile)
            {
                Log.WriteInfo(nameof(Settings), "File logging is disabled");
            }

            Current.IRC.Enabled = CanConnectToIRC();
        }

        private static bool CanConnectToIRC()
        {
            if (FullRun != FullRunState.None)
            {
                // Don't connect to IRC while doing a full run
                return false;
            }

            if (!Current.IRC.Enabled)
            {
                Log.WriteInfo(nameof(Settings), "IRC is disabled in settings");
                return false;
            }

            if (string.IsNullOrEmpty(Current.IRC.Server) || Current.IRC.Port <= 0)
            {
                Log.WriteError(nameof(Settings), "Missing IRC details in settings file, not connecting");
                return false;
            }

            if (string.IsNullOrWhiteSpace(Current.IRC.Nickname))
            {
                Log.WriteError(nameof(Settings), "Missing IRC nickname in settings file, not connecting");
                return false;
            }

            if (string.IsNullOrWhiteSpace(Current.IRC.Password))
            {
                Current.IRC.Password = null;
            }

            return true;
        }
    }
}
