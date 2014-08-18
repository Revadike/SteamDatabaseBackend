/*
 * Copyright (c) 2013, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.IO;
using System.Net;
using SteamKit2;

namespace SteamDatabaseBackend
{
    class BinariesCommand : Command
    {
        private const string CDN = "https://steamcdn-a.akamaihd.net/client/";

        public BinariesCommand()
        {
            Trigger = "!bins";
        }

        public override void OnCommand(CommandArguments command)
        {
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

                    if (kv["bins_osx"].Children.Count == 0)
                    {
                        CommandHandler.ReplyToCommand(command, "Failed to find binaries in parsed response.");

                        return;
                    }

                    kv = kv["bins_osx"];

                    CommandHandler.ReplyToCommand(command, "You're on your own:{0} {1}{2} {3}({4} MB)", Colors.DARK_BLUE, CDN, kv["file"].AsString(), Colors.DARK_GRAY, (kv["size"].AsLong() / 1048576.0).ToString("0.###"));
                };

                webClient.DownloadDataAsync(new Uri(string.Format("{0}steam_client_publicbeta_osx?_={1}", CDN, DateTime.UtcNow.Ticks)));
            }
        }
    }
}
