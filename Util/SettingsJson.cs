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
    // Compiler complains that none of the fields are ever assigned
    // But it's only every used for de-serializing JSON, which makes sure that all the fields are present
    #pragma warning disable 0649
    sealed class SettingsJson
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
            public bool Ssl;

            [JsonProperty(Required = Required.Always)]
            public bool SslAcceptInvalid;

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
            public char CommandPrefix;

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
        public List<Uri> RssFeeds;

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
    #pragma warning restore 0649
}
