/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections.Generic;
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
            if (!IRC.IsRecipientChannel(command.Recipient))
            {
                CommandHandler.ReplyToCommand(command, "This command is only available in channels.");

                return;
            }

            var channel = command.Recipient;

            if (channel == Settings.Current.IRC.Channel.Announce)
            {
                CommandHandler.ReplyToCommand(command, "This command is not available in announcement channel.");

                return;
            }

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
                                        List<string> channels;
                                        var exists = Application.ImportantApps.TryGetValue(id, out channels);

                                        if (exists && channels.Contains(channel))
                                        {
                                            CommandHandler.ReplyToCommand(command, "App {0}{1}{2} ({3}) is already important in {4}{5}{6}.", Colors.BLUE, id, Colors.NORMAL, Steam.GetAppName(id), Colors.BLUE, channel, Colors.NORMAL);
                                        }
                                        else
                                        {
                                            if (exists)
                                            {
                                                Application.ImportantApps[id].Add(channel);
                                            }
                                            else
                                            {
                                                Application.ImportantApps.Add(id, new List<string>{ channel });
                                            }

                                            DbWorker.ExecuteNonQuery("INSERT INTO `ImportantApps` (`AppID`, `Channel`) VALUES (@AppID, @Channel)", new MySqlParameter("AppID", id), new MySqlParameter("Channel", channel));

                                            CommandHandler.ReplyToCommand(command, "Marked app {0}{1}{2} ({3}) as important in {4}{5}{6}.", Colors.BLUE, id, Colors.NORMAL, Steam.GetAppName(id), Colors.BLUE, channel, Colors.NORMAL);
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
                                        List<string> channels;

                                        if (!Application.ImportantApps.TryGetValue(id, out channels) || !channels.Contains(channel))
                                        {
                                            CommandHandler.ReplyToCommand(command, "App {0}{1}{2} ({3}) is not important in {4}{5}{6}.", Colors.BLUE, id, Colors.NORMAL, Steam.GetAppName(id), Colors.BLUE, channel, Colors.NORMAL);
                                        }
                                        else
                                        {
                                            if (channels.Count > 1)
                                            {
                                                Application.ImportantApps[id].Remove(channel);
                                            }
                                            else
                                            {
                                                Application.ImportantApps.Remove(id);
                                            }

                                            DbWorker.ExecuteNonQuery("DELETE FROM `ImportantApps` WHERE `AppID` = @AppID AND `Channel` = @Channel", new MySqlParameter("AppID", id), new MySqlParameter("Channel", channel));

                                            CommandHandler.ReplyToCommand(command, "Removed app {0}{1}{2} ({3}) from the important list in {4}{5}{6}.", Colors.BLUE, id, Colors.NORMAL, Steam.GetAppName(id), Colors.BLUE, channel, Colors.NORMAL);
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

                    case "queue":
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
                                        using (var reader = DbWorker.ExecuteReader("SELECT `Name` FROM `Apps` WHERE `AppID` = @AppID", new MySqlParameter("AppID", id)))
                                        {
                                            if (reader.Read())
                                            {
                                                StoreQueue.AddAppToQueue(id);

                                                CommandHandler.ReplyToCommand(command, "App {0}{1}{2} ({3}) has been added to the store update queue.", Colors.BLUE, id, Colors.NORMAL, Utils.RemoveControlCharacters(reader.GetString("Name")));

                                                return;
                                            }
                                        }

                                        CommandHandler.ReplyToCommand(command, "This app is not in the database.");

                                        return;
                                    }

                                case "sub":
                                    {
                                        if (id == 0)
                                        {
                                            CommandHandler.ReplyToCommand(command, "Sub 0 can not be queued.");

                                            return;
                                        }

                                        using (var reader = DbWorker.ExecuteReader("SELECT `Name` FROM `Subs` WHERE `SubID` = @SubID", new MySqlParameter("SubID", id)))
                                        {
                                            if (reader.Read())
                                            {
                                                StoreQueue.AddPackageToQueue(id);

                                                CommandHandler.ReplyToCommand(command, "Package {0}{1}{2} ({3}) has been added to the store update queue.", Colors.BLUE, id, Colors.NORMAL, Utils.RemoveControlCharacters(reader.GetString("Name")));

                                                return;
                                            }
                                        }

                                        CommandHandler.ReplyToCommand(command, "This package is not in the database.");

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
