/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System.Threading.Tasks;
using Dapper;

namespace SteamDatabaseBackend
{
    internal class ImportantCommand : Command
    {
        public ImportantCommand()
        {
            Trigger = "important";
            IsAdminCommand = true;
        }

        public override async Task OnCommand(CommandArguments command)
        {
            var s = command.Message.Split(' ');
            var count = s.Length;

            if (count > 0)
            {
                uint id;
                switch (s[0])
                {
                    case "reload":
                        await Application.ReloadImportant();
                        await PICSTokens.Reload();

                        command.Notice("Reloaded important apps and pics tokens");

                        return;

                    case "fullrun":
                        _ = TaskManager.Run(async () =>
                        {
                            command.Reply("Started full metadata scan, this will take a while…");
                            await FullUpdateProcessor.FullUpdateAppsMetadata(true);
                            command.Reply("App full scan finished, starting packages, this will take even longer…");
                            await FullUpdateProcessor.FullUpdatePackagesMetadata();
                            command.Reply("Full metadata scan finished.");
                        });

                        return;

                    case "add":
                        if (count < 3)
                        {
                            break;
                        }

                        if (!uint.TryParse(s[2], out id))
                        {
                            break;
                        }

                        switch (s[1])
                        {
                            case "app":
                                if (Application.ImportantApps.Contains(id))
                                {
                                    command.Reply($"App {Colors.BLUE}{id}{Colors.NORMAL} ({Steam.GetAppName(id)}) is already important.");
                                }
                                else
                                {
                                    Application.ImportantApps.Add(id);

                                    await using (var db = await Database.GetConnectionAsync())
                                    {
                                        await db.ExecuteAsync("INSERT INTO `ImportantApps` (`AppID`) VALUES (@AppID)", new { AppID = id });
                                    }

                                    command.Reply($"Marked app {Colors.BLUE}{id}{Colors.NORMAL} ({Steam.GetAppName(id)}) as important.");
                                }

                                return;

                            case "sub":
                                if (Application.ImportantSubs.Contains(id))
                                {
                                    command.Reply($"Package {Colors.BLUE}{id}{Colors.NORMAL} ({Steam.GetPackageName(id)}) is already important.");
                                }
                                else
                                {
                                    Application.ImportantSubs.Add(id);

                                    await using (var db = await Database.GetConnectionAsync())
                                    {
                                        await db.ExecuteAsync("INSERT INTO `ImportantSubs` (`SubID`) VALUES (@SubID)", new { SubID = id });
                                    }

                                    command.Reply($"Marked package {Colors.BLUE}{id}{Colors.NORMAL} ({Steam.GetPackageName(id)}) as important.");
                                }

                                return;
                        }

                        break;

                    case "remove":
                        if (count < 3)
                        {
                            break;
                        }

                        if (!uint.TryParse(s[2], out id))
                        {
                            break;
                        }

                        switch (s[1])
                        {
                            case "app":
                                if (!Application.ImportantApps.Contains(id))
                                {
                                    command.Reply($"App {Colors.BLUE}{id}{Colors.NORMAL} ({Steam.GetAppName(id)}) is not important.");
                                }
                                else
                                {
                                    Application.ImportantApps.Remove(id);

                                    await using (var db = await Database.GetConnectionAsync())
                                    {
                                        await db.ExecuteAsync("DELETE FROM `ImportantApps` WHERE `AppID` = @AppID", new { AppID = id });
                                    }

                                    command.Reply($"Removed app {Colors.BLUE}{id}{Colors.NORMAL} ({Steam.GetAppName(id)}) from the important list.");
                                }

                                return;

                            case "sub":
                                if (!Application.ImportantSubs.Contains(id))
                                {
                                    command.Reply($"Package {Colors.BLUE}{id}{Colors.NORMAL} ({Steam.GetPackageName(id)}) is not important.");
                                }
                                else
                                {
                                    Application.ImportantSubs.Remove(id);

                                    await using (var db = await Database.GetConnectionAsync())
                                    {
                                        await db.ExecuteAsync("DELETE FROM `ImportantSubs` WHERE `SubID` = @SubID", new { SubID = id });
                                    }

                                    command.Reply($"Removed package {Colors.BLUE}{id}{Colors.NORMAL} ({Steam.GetPackageName(id)}) from the important list.");
                                }

                                return;
                        }

                        break;
                }
            }

            command.Reply($"Usage:{Colors.OLIVE} important reload {Colors.NORMAL}or{Colors.OLIVE} important <add/remove> <app/sub> <id> {Colors.NORMAL}or{Colors.OLIVE} important fullrun");
        }
    }
}
