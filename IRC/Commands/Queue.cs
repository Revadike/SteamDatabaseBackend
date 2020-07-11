/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System.Threading.Tasks;

namespace SteamDatabaseBackend
{
    internal class QueueCommand : Command
    {
        public QueueCommand()
        {
            Trigger = "queue";
        }

        public override async Task OnCommand(CommandArguments command)
        {
            var s = command.Message.Split(' ');

            if (s.Length < 2)
            {
                command.Reply($"Usage:{Colors.OLIVE} queue <app/sub> <id>");

                return;
            }

            if (!uint.TryParse(s[1], out var id))
            {
                command.Reply("Your ID does not look like a number.");

                return;
            }

            switch (s[0])
            {
                case "app":
                    var appName = Steam.GetAppName(id);

                    if (!string.IsNullOrEmpty(appName))
                    {
                        await StoreQueue.AddAppToQueue(id);

                        command.Reply($"App {Colors.BLUE}{id}{Colors.NORMAL} ({appName}) has been added to the store update queue.");

                        return;
                    }

                    command.Reply("This app is not in the database.");

                    return;

                case "package":
                case "sub":
                    if (id == 0)
                    {
                        command.Reply("Sub 0 can not be queued.");

                        return;
                    }

                    var subName = Steam.GetPackageName(id);

                    if (!string.IsNullOrEmpty(subName))
                    {
                        await StoreQueue.AddPackageToQueue(id);

                        command.Reply($"Package {Colors.BLUE}{id}{Colors.NORMAL} ({subName}) has been added to the store update queue.");

                        return;
                    }

                    command.Reply("This package is not in the database.");

                    return;

                default:
                    command.Reply("You can only queue apps and packages.");

                    return;
            }
        }
    }
}
