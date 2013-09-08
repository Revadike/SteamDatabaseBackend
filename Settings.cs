/*
 * Copyright (c) 2013, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.IO;
using Newtonsoft.Json;

namespace SteamDatabaseBackend
{
    public static class Settings
    {
        public class SettingsJson
        {
            public class SteamJson
            {
                public string Username;
                public string Password;
            }

            public class IrcJson
            {
                public bool Enabled;
                public string[] Servers;
                public int Port;
                public string Nickname;
                public IrcChannelsJson Channel;
            }

            public class IrcChannelsJson
            {
                public string Main;
                public string Announce;
            }

            public SteamJson Steam;
            public SteamJson SteamDota;
            public IrcJson IRC;

            public string ConnectionString;
            public uint FullRun;
            public bool SteamKitDebug;
        }

        public static SettingsJson Current;

        public static void Load()
        {
            string settingsFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");

            if (!File.Exists(settingsFile))
            {
                throw new Exception("Settings file not found, must be in settings.json");
            }

            Current = JsonConvert.DeserializeObject<SettingsJson>(File.ReadAllText(settingsFile), new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error });

            if (string.IsNullOrWhiteSpace(Current.Steam.Username) || string.IsNullOrWhiteSpace(Current.Steam.Password))
            {
                throw new Exception("Missing Steam credentials in settings file");
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

        public static bool CanUseDota()
        {
            if (string.IsNullOrWhiteSpace(Current.SteamDota.Username) || string.IsNullOrWhiteSpace(Current.SteamDota.Password))
            {
                Log.WriteWarn("Settings", "Missing Steam credentials for Dota 2 account in settings file");
                return false;
            }

            return true;
        }
    }
}
