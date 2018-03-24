/*
 * Copyright (c) 2013-2018, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SteamDatabaseBackend
{
    class PackageCommand : Command
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
                command.Reply("Usage:{0} sub <subid>", Colors.OLIVE);

                return;
            }

            var job = await Steam.Instance.Apps.PICSGetProductInfo(null, subID, false, false);
            var callback = job.Results.FirstOrDefault(x => x.Packages.ContainsKey(subID));

            if (callback == null)
            {
                command.Reply("Unknown SubID: {0}{1}{2}", Colors.BLUE, subID, LicenseList.OwnedSubs.ContainsKey(subID) ? SteamDB.StringCheckmark : string.Empty);

                return;
            }

            var info = callback.Packages[subID];

            info.KeyValues.SaveToFile(Path.Combine(Application.Path, "sub", string.Format("{0}.vdf", info.ID)), false);

            command.Reply("{0}{1}{2} -{3} {4}{5} - Dump:{6} {7}{8}{9}{10}",
                Colors.BLUE, Steam.GetPackageName(info.ID), Colors.NORMAL,
                Colors.DARKBLUE, SteamDB.GetPackageURL(info.ID), Colors.NORMAL,
                Colors.DARKBLUE, SteamDB.GetRawPackageURL(info.ID), Colors.NORMAL,
                info.MissingToken ? SteamDB.StringNeedToken : string.Empty,
                LicenseList.OwnedSubs.ContainsKey(info.ID) ? SteamDB.StringCheckmark : string.Empty
            );
        }
    }
}
