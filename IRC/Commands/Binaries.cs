/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
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

        public BinariesCommand()
        {
            Trigger = "bins";
        }

        public override void OnCommand(CommandArguments command)
        {
            if (command.Message.Length == 0)
            {
                CommandHandler.ReplyToCommand(command, "Usage:{0} bins <{1}> [stable (returns publicbeta by default)]", Colors.OLIVE, string.Join("/", Systems));

                return;
            }

            var args = command.Message.Split(' ');
            string os = args[0];

            if (!Systems.Contains(os))
            {
                CommandHandler.ReplyToCommand(command, "Invalid OS. Valid ones are: {0}", string.Join(", ", Systems));

                return;
            }

            using (var webClient = new WebClient())
            {
                webClient.DownloadDataCompleted += delegate(object sender, DownloadDataCompletedEventArgs e)
                {
                    var kv = new KeyValue();

                    using (var ms = new MemoryStream(e.Result))
                    {
                        try
                        {
                            kv.ReadAsText(ms);
                        }
                        catch
                        {
                            CommandHandler.ReplyToCommand(command, "Something went horribly wrong and keyvalue parser died.");

                            return;
                        }
                    }

                    PrintBinary(command, kv, string.Concat("bins_", os));
                    PrintBinary(command, kv, string.Concat("bins_client_", os));
                };

                var isStable = args.Length > 1 && args[1].Equals("stable");

                webClient.DownloadDataAsync(new Uri(string.Format("{0}steam_client_{1}{2}?_={3}", CDN, isStable ? "" : "publicbeta_", os, DateTime.UtcNow.Ticks)));
            }
        }

        private static bool PrintBinary(CommandArguments command, KeyValue kv, string key)
        {
            if (kv[key].Children.Count == 0)
            {
                return false;
            }

            kv = kv[key];

            CommandHandler.ReplyToCommand(command, "{0}{1} {2}({3} MB)", CDN, kv["file"].AsString(), Colors.DARKGRAY, (kv["size"].AsLong() / 1048576.0).ToString("0.###"));

            return true;
        }
    }
}
