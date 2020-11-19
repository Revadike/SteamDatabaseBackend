/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SteamKit2;

namespace SteamDatabaseBackend
{
    internal class PackageCommand : Command
    {
        public PackageCommand()
        {
            Trigger = "sub";
            IsSteamCommand = true;
        }

        public override async Task OnCommand(CommandArguments command)
        {
            if (command.Message.Length == 0 || !uint.TryParse(command.Message, out var subID))
            {
                command.Reply($"Usage:{Colors.OLIVE} sub <subid>");

                return;
            }

            var info = await GetPackageData(subID);

            if (info == null)
            {
                command.Reply($"Unknown SubID: {Colors.BLUE}{subID}{(LicenseList.OwnedSubs.ContainsKey(subID) ? SteamDB.StringCheckmark : string.Empty)}");

                return;
            }

            if (!info.KeyValues.Children.Any())
            {
                command.Reply($"No package info returned for SubID: {Colors.BLUE}{subID}{(info.MissingToken ? SteamDB.StringNeedToken : string.Empty)}{(LicenseList.OwnedSubs.ContainsKey(subID) ? SteamDB.StringCheckmark : string.Empty)}");

                return;
            }

            info.KeyValues.SaveToFile(Path.Combine(Application.Path, "sub", $"{info.ID}.vdf"), false);

            command.Reply($"{Colors.BLUE}{Steam.GetPackageName(info.ID)}{Colors.NORMAL} -{Colors.DARKBLUE} {SteamDB.GetPackageUrl(info.ID)}{Colors.NORMAL} - Dump:{Colors.DARKBLUE} {SteamDB.GetRawPackageUrl(info.ID)}{Colors.NORMAL}{(info.MissingToken ? SteamDB.StringNeedToken : string.Empty)}{(LicenseList.OwnedSubs.ContainsKey(info.ID) ? SteamDB.StringCheckmark : string.Empty)}");
        }

        public static async Task<SteamApps.PICSProductInfoCallback.PICSProductInfo> GetPackageData(uint subID)
        {
            var tokenTask = Steam.Instance.Apps.PICSGetAccessTokens(null, subID);
            tokenTask.Timeout = TimeSpan.FromSeconds(10);
            var tokenCallback = await tokenTask;
            SteamApps.PICSRequest request;

            if (tokenCallback.PackageTokens.ContainsKey(subID))
            {
                request = PICSTokens.NewPackageRequest(subID, tokenCallback.PackageTokens[subID]);
            }
            else
            {
                request = PICSTokens.NewPackageRequest(subID);
            }

            var infoTask = Steam.Instance.Apps.PICSGetProductInfo(Enumerable.Empty<SteamApps.PICSRequest>(), new List<SteamApps.PICSRequest> { request });
            infoTask.Timeout = TimeSpan.FromSeconds(10);
            var job = await infoTask;

            return job.Results?.FirstOrDefault(x => x.Packages.ContainsKey(subID))?.Packages[subID];
        }
    }
}
