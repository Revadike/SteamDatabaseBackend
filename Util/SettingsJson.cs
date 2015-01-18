/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace SteamDatabaseBackend
{
    public sealed class SettingsJson
    {
        public sealed class SteamJson
        {
            [JsonProperty(Required = Required.Always)]
            public string Username;

            [JsonProperty(Required = Required.Always)]
            public string Password;

            [JsonProperty(Required = Required.Always)]
            public string WebAPIKey;
        }

        public sealed class IrcJson
        {
            [JsonProperty(Required = Required.Always)]
            public bool Enabled;

            [JsonProperty(Required = Required.Always)]
            public string Server;

            [JsonProperty(Required = Required.Always)]
            public List<string> Admins;

            [JsonProperty(Required = Required.Always)]
            public int Port;

            [JsonProperty(Required = Required.Always)]
            public string Nickname;

            [JsonProperty(Required = Required.Always)]
            public string Password;

            [JsonProperty(Required = Required.Always)]
            public uint SendDelay;

            [JsonProperty(Required = Required.Always)]
            public IrcChannelsJson Channel;

            [JsonProperty(Required = Required.Always)]
            public string PrioritySendPrefix;
        }

        public sealed class IrcChannelsJson
        {
            [JsonProperty(Required = Required.Always)]
            public string Ops;

            [JsonProperty(Required = Required.Always)]
            public string Main;

            [JsonProperty(Required = Required.Always)]
            public string Announce;
        }

        [JsonProperty(Required = Required.Always)]
        public List<ulong> SteamAdmins;

        [JsonProperty(Required = Required.Always)]
        public List<ulong> ChatRooms;

        [JsonProperty(Required = Required.Always)]
        public List<uint> GameCoordinatorIdlers;

        [JsonProperty(Required = Required.Always)]
        public SteamJson Steam;

        [JsonProperty(Required = Required.Always)]
        public IrcJson IRC;

        [JsonProperty(Required = Required.Always)]
        public Uri BaseURL;

        [JsonProperty(Required = Required.Always)]
        public Uri RawBaseURL;

        [JsonProperty(Required = Required.Always)]
        public string ConnectionString;

        [JsonProperty(Required = Required.AllowNull)]
        public string BugsnagApiKey;

        [JsonProperty(Required = Required.Always)]
        public uint FullRun;

        [JsonProperty(Required = Required.Always)]
        public bool SteamKitDebug;

        [JsonProperty(Required = Required.Always)]
        public bool LogToFile;
    }
}
