/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SteamKit2;

namespace SteamDatabaseBackend
{
    internal class BinariesCommand : Command
    {
        private const string CDN = "https://steamcdn-a.akamaihd.net/client/";

        private static readonly List<string> Systems = new List<string>
        {
            "osx",
            "win32",
            "ubuntu12"
        };

        private static readonly IDictionary<string, string> SystemAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["windows"] = "win32",
            ["linux"] = "ubuntu12",
            ["macos"] = "osx"
        };

        public BinariesCommand()
        {
            Trigger = "bins";
        }

        public override async Task OnCommand(CommandArguments command)
        {
            if (command.Message.Length == 0)
            {
                command.Reply($"Usage:{Colors.OLIVE} bins <{string.Join("/", Systems)}> [stable (returns publicbeta by default)]");

                return;
            }

            var args = command.Message.Split(' ');
            var os = args[0];

            if (SystemAliases.TryGetValue(os, out var aliasTarget))
            {
                os = aliasTarget;
            }

            if (!Systems.Contains(os))
            {
                command.Reply($"Invalid OS. Valid ones are: {string.Join(", ", Systems)}");

                return;
            }

            var isStable = args.Length > 1 && args[1] == "stable";
            var uri = new Uri($"{CDN}steam_client_{(isStable ? "" : "publicbeta_")}{os}?_={DateTime.UtcNow.Ticks}");

            var client = Utils.HttpClient;
            var data = await client.GetStringAsync(uri);
            var kv = KeyValue.LoadFromString(data);

            if (kv == null)
            {
                throw new InvalidOperationException("Failed to parse downloaded client manifest.");
            }

            PrintBinary(command, kv, string.Concat("bins_", os));
            PrintBinary(command, kv, string.Concat("bins_client_", os));
        }

        private static void PrintBinary(CommandArguments command, KeyValue kv, string key)
        {
            if (kv[key].Children.Count == 0)
            {
                return;
            }

            kv = kv[key];

            command.Reply($"{CDN}{kv["file"].AsString()} {Colors.DARKGRAY}({(kv["size"].AsLong() / 1048576.0):0.###} MB)");
        }
    }
}
