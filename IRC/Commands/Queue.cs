/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System.Threading.Tasks;
using Dapper;

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
                command.Reply("Usage:{0} queue <app/sub> <id>", Colors.OLIVE);

                return;
            }

            if (!uint.TryParse(s[1], out var id))
            {
                command.Reply("Your ID does not look like a number.");

                return;
            }

            string name;

            switch (s[0])
            {
                case "app":
                    using (var db = Database.Get())
                    {
                        name = await db.ExecuteScalarAsync<string>("SELECT `Name` FROM `Apps` WHERE `AppID` = @AppID", new { AppID = id });
                    }

                    if (!string.IsNullOrEmpty(name))
                    {
                        await StoreQueue.AddAppToQueue(id);

                        command.Reply("App {0}{1}{2} ({3}) has been added to the store update queue.", Colors.BLUE, id, Colors.NORMAL, Utils.RemoveControlCharacters(name));

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

                    using (var db = Database.Get())
                    {
                        name = await db.ExecuteScalarAsync<string>("SELECT `Name` FROM `Subs` WHERE `SubID` = @SubID", new { SubID = id });
                    }

                    if (!string.IsNullOrEmpty(name))
                    {
                        await StoreQueue.AddPackageToQueue(id);

                        command.Reply("Package {0}{1}{2} ({3}) has been added to the store update queue.", Colors.BLUE, id, Colors.NORMAL, Utils.RemoveControlCharacters(name));

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
