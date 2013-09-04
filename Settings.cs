/*
 * Copyright (c) 2013, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.IO;
using Newtonsoft.Json;

namespace PICSUpdater
{
    public static class Settings
    {
        public static SettingsJSON Current;

        public static void Load()
        {
            string settingsFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");

            if (!File.Exists(settingsFile))
            {
                Log.WriteError("Settings", "Settings file not found, must be in settings.json");

                Environment.Exit(0);

                return;
            }

            Current = JsonConvert.DeserializeObject<SettingsJSON>(File.ReadAllText(settingsFile)) as SettingsJSON;
        }

        public static bool Validate()
        {
            if (string.IsNullOrWhiteSpace(Current.Steam.Username) || string.IsNullOrWhiteSpace(Current.Steam.Password))
            {
                Log.WriteError("Settings", "Missing Steam credentials in settings file");
                return false;
            }

            return true;
        }

        public static bool CanConnectToIRC()
        {
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

    public class SettingsJSON
    {
        public class SteamJSON
        {
            public string Username;
            public string Password;
        }

        public class IRCJSON
        {
            public string[] Servers;
            public int Port;
            public string Nickname;
            public IRCChannelsJSON Channel;
        }

        public class IRCChannelsJSON
        {
            public string Main;
            public string Announce;
        }

        public SteamJSON Steam;
        public SteamJSON SteamDota;
        public IRCJSON IRC;

        public string ConnectionString;
        public uint FullRun;
        public bool SteamKitDebug;
    }
}
