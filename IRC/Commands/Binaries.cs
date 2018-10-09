/*
 * Copyright (c) 2013-2018, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using SteamKit2;

namespace SteamDatabaseBackend
{
    class BinariesCommand : Command
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
                command.Reply("Usage:{0} bins <{1}> [stable (returns publicbeta by default)]", Colors.OLIVE, string.Join("/", Systems));

                return;
            }

            var args = command.Message.Split(' ');
            string os = args[0];

            if (SystemAliases.TryGetValue(os, out var aliasTarget))
            {
                os = aliasTarget;
            }

            if (!Systems.Contains(os))
            {
                command.Reply("Invalid OS. Valid ones are: {0}", string.Join(", ", Systems));

                return;
            }

            using (var webClient = new WebClient())
            {
                var isStable = args.Length > 1 && args[1].Equals("stable");
                string data = await webClient.DownloadStringTaskAsync(new Uri(string.Format("{0}steam_client_{1}{2}?_={3}", CDN, isStable ? "" : "publicbeta_", os, DateTime.UtcNow.Ticks)));

                var kv = KeyValue.LoadFromString(data);

                if (kv == null)
                {
                    throw new Exception("Failed to parse downloaded client manifest.");
                }

                PrintBinary(command, kv, string.Concat("bins_", os));
                PrintBinary(command, kv, string.Concat("bins_client_", os));
            }
        }

        private static void PrintBinary(CommandArguments command, KeyValue kv, string key)
        {
            if (kv[key].Children.Count == 0)
            {
                return;
            }

            kv = kv[key];

            command.Reply("{0}{1} {2}({3} MB)", CDN, kv["file"].AsString(), Colors.DARKGRAY, (kv["size"].AsLong() / 1048576.0).ToString("0.###"));
        }
    }
}
