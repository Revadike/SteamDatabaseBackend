/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
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
    internal sealed class SettingsJson
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
            public string Server;

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
        }

        public sealed class IrcChannelsJson
        {
            [JsonProperty(Required = Required.Always)]
            public string Ops;

            [JsonProperty(Required = Required.Always)]
            public string Announce;
        }

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
        public Uri WebhookURL;

        [JsonProperty(Required = Required.Always)]
        public string ConnectionString;

        [JsonProperty(Required = Required.Always)]
        public bool LogToFile;

        [JsonProperty(Required = Required.Always)]
        public bool OnlyOwnedDepots;

        [JsonProperty(Required = Required.Always)]
        public uint BuiltInHttpServerPort;
    }
#pragma warning restore 0649
}
