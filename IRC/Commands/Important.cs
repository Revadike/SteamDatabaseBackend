/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using MySql.Data.MySqlClient;

namespace SteamDatabaseBackend
{
    class ImportantCommand : Command
    {
        public ImportantCommand()
        {
            Trigger = "!important";
            IsAdminCommand = true;
        }

        public override void OnCommand(CommandArguments command)
        {
            var s = command.Message.Split(' ');
            var count = s.Length;

            if (count > 0)
            {
                switch (s[0])
                {
                    case "reload":
                        {
                            Application.ReloadImportant(command);

                            return;
                        }

                    case "add":
                        {
                            if (count < 3)
                            {
                                break;
                            }

                            uint id;

                            if (!uint.TryParse(s[2], out id))
                            {
                                break;
                            }

                            switch (s[1])
                            {
                                case "app":
                                    {
                                        if (Application.ImportantApps.ContainsKey(id))
                                        {
                                            CommandHandler.ReplyToCommand(command, "App {0}{1}{2} ({3}) is already important.", Colors.BLUE, id, Colors.NORMAL, Steam.GetAppName(id));
                                        }
                                        else
                                        {
                                            Application.ImportantApps.Add(id, 1);

                                            DbWorker.ExecuteNonQuery("INSERT INTO `ImportantApps` (`AppID`, `Announce`) VALUES (@AppID, 1) ON DUPLICATE KEY UPDATE `Announce` = 1", new MySqlParameter("AppID", id));

                                            CommandHandler.ReplyToCommand(command, "Marked app {0}{1}{2} ({3}) as important.", Colors.BLUE, id, Colors.NORMAL, Steam.GetAppName(id));
                                        }

                                        return;
                                    }

                                case "sub":
                                    {
                                        if (Application.ImportantSubs.ContainsKey(id))
                                        {
                                            CommandHandler.ReplyToCommand(command, "Package {0}{1}{2} ({3}) is already important.", Colors.BLUE, id, Colors.NORMAL, Steam.GetPackageName(id));
                                        }
                                        else
                                        {
                                            Application.ImportantSubs.Add(id, 1);

                                            DbWorker.ExecuteNonQuery("INSERT INTO `ImportantSubs` (`SubID`) VALUES (@SubID)", new MySqlParameter("SubID", id));

                                            CommandHandler.ReplyToCommand(command, "Marked package {0}{1}{2} ({3}) as important.", Colors.BLUE, id, Colors.NORMAL, Steam.GetPackageName(id));
                                        }

                                        return;
                                    }
                            }

                            break;
                        }

                    case "remove":
                        {
                            if (count < 3)
                            {
                                break;
                            }

                            uint id;

                            if (!uint.TryParse(s[2], out id))
                            {
                                break;
                            }

                            switch (s[1])
                            {
                                case "app":
                                    {
                                        if (!Application.ImportantApps.ContainsKey(id))
                                        {
                                            CommandHandler.ReplyToCommand(command, "App {0}{1}{2} ({3}) is not important.", Colors.BLUE, id, Colors.NORMAL, Steam.GetAppName(id));
                                        }
                                        else
                                        {
                                            Application.ImportantApps.Remove(id);

                                            DbWorker.ExecuteNonQuery("UPDATE `ImportantApps` SET `Announce` = 0 WHERE `AppID` = @AppID", new MySqlParameter("AppID", id));

                                            CommandHandler.ReplyToCommand(command, "Removed app {0}{1}{2} ({3}) from the important list.", Colors.BLUE, id, Colors.NORMAL, Steam.GetAppName(id));
                                        }

                                        return;
                                    }

                                case "sub":
                                    {
                                        if (!Application.ImportantSubs.ContainsKey(id))
                                        {
                                            CommandHandler.ReplyToCommand(command, "Package {0}{1}{2} ({3}) is not important.", Colors.BLUE, id, Colors.NORMAL, Steam.GetPackageName(id));
                                        }
                                        else
                                        {
                                            Application.ImportantSubs.Remove(id);

                                            DbWorker.ExecuteNonQuery("DELETE FROM `ImportantSubs` WHERE `SubID` = @SubID", new MySqlParameter("SubID", id));

                                            CommandHandler.ReplyToCommand(command, "Removed package {0}{1}{2} ({3}) from the important list.", Colors.BLUE, id, Colors.NORMAL, Steam.GetPackageName(id));
                                        }

                                        return;
                                    }
                            }

                            break;
                        }
                }
            }

            CommandHandler.ReplyToCommand(command, "Usage:{0} !important reload {1}or{2} !important <add/remove> <app/sub> <id>", Colors.OLIVE, Colors.NORMAL, Colors.OLIVE);
        }
    }
}
