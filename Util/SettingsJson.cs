/*
 * Copyright (c) 2013-2018, SteamDB. All rights reserved.
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

            [JsonProperty(Required = Required.Default)]
            public Uri WebAPIUrl = SteamKit2.WebAPI.DefaultBaseAddress;
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
            public List<string> Admins = new List<string>();

            [JsonProperty(Required = Required.Always)]
            public int Port;

            [JsonProperty(Required = Required.Always)]
            public string Nickname;

            [JsonProperty(Required = Required.Always)]
            public string Password;

            [JsonProperty(Required = Required.Always)]
            public char CommandPrefix;

            [JsonProperty(Required = Required.Always)]
            public IrcChannelsJson Channel = new IrcChannelsJson();

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
        public List<ulong> SteamAdmins = new List<ulong>();

        [JsonProperty(Required = Required.Always)]
        public List<ulong> ChatRooms = new List<ulong>();

        [JsonProperty(Required = Required.Always)]
        public List<uint> GameCoordinatorIdlers = new List<uint>();

        [JsonProperty(Required = Required.Always)]
        public SteamJson Steam = new SteamJson();

        [JsonProperty(Required = Required.Always)]
        public IrcJson IRC = new IrcJson();

        [JsonProperty(Required = Required.Always)]
        public List<Uri> RssFeeds = new List<Uri>();

        [JsonProperty(Required = Required.Always)]
        public Uri BaseURL;

        [JsonProperty(Required = Required.Always)]
        public Uri RawBaseURL;

        [JsonProperty(Required = Required.Default)]
        public string PatchnotesNotifyURL;

        [JsonProperty(Required = Required.Always)]
        public string ConnectionString;

        [JsonProperty(Required = Required.Always)]
        public FullRunState FullRun;

        [JsonProperty(Required = Required.Always)]
        public bool SteamKitDebug;

        [JsonProperty(Required = Required.Always)]
        public bool LogToFile;

        [JsonProperty(Required = Required.Always)]
        public bool CanQueryStore;
    }
    #pragma warning restore 0649
}
