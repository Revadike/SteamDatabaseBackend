/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Threading.Tasks;
using SteamKit2;

namespace SteamDatabaseBackend
{
    internal class UGCCommand : Command
    {
        private readonly SteamCloud Cloud;

        public UGCCommand()
        {
            Trigger = "ugc";
            IsSteamCommand = true;

            Cloud = Steam.Instance.Client.GetHandler<SteamCloud>();
        }

        public override async Task OnCommand(CommandArguments command)
        {
            if (command.Message.Length == 0)
            {
                command.Reply($"Usage:{Colors.OLIVE} ugc <ugcid>");

                return;
            }

            if (!ulong.TryParse(command.Message, out var ugcId))
            {
                command.Reply("Invalid UGC ID");

                return;
            }

            var task = Cloud.RequestUGCDetails(ugcId);
            task.Timeout = TimeSpan.FromSeconds(10);
            var callback = await task;

            if (callback.Result != EResult.OK)
            {
                command.Reply($"Unable to request UGC info: {Colors.RED}{callback.Result}");

                return;
            }

            command.Reply($"Creator: {Colors.BLUE}{callback.Creator.Render()}{Colors.NORMAL}, App: {Colors.BLUE}{callback.AppID}{Colors.NORMAL}, File: {Colors.BLUE}{callback.FileName}{Colors.NORMAL}, Size: {Colors.BLUE}{GetByteSizeString(callback.FileSize)}{Colors.NORMAL} -{Colors.DARKBLUE} {callback.URL}");
        }

        private static string GetByteSizeString(uint size)
        {
            string[] suf = { "B", "KB", "MB", "GB" };

            if (size == 0)
            {
                return "0B";
            }

            var place = Convert.ToInt32(Math.Floor(Math.Log(size, 1024)));
            var num = Math.Round(size / Math.Pow(1024, place), 1);
            return (Math.Sign(size) * num) + suf[place];
        }
    }
}
