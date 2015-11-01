/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System.Collections.Generic;
using System.Threading.Tasks;
using Dapper;

namespace SteamDatabaseBackend
{
    class ImportantCommand : Command
    {
        public ImportantCommand()
        {
            Trigger = "important";
            IsAdminCommand = true;
        }

        public override async Task OnCommand(CommandArguments command)
        {
            await Task.Yield();

            if (command.CommandType != ECommandType.IRC || !IRC.IsRecipientChannel(command.Recipient))
            {
                command.Reply("This command is only available in channels.");

                return;
            }

            var channel = command.Recipient;

            var s = command.Message.Split(' ');
            var count = s.Length;
            uint id;

            if (count > 0)
            {
                switch (s[0])
                {
                    case "reload":
                        Application.ReloadImportant(command);
                        PICSTokens.Reload(command);
                        FileDownloader.ReloadFileList();

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
                                List<string> channels;
                                var exists = Application.ImportantApps.TryGetValue(id, out channels);

                                if (exists && channels.Contains(channel))
                                {
                                    command.Reply("App {0}{1}{2} ({3}) is already important in {4}{5}{6}.", Colors.BLUE, id, Colors.NORMAL, Steam.GetAppName(id), Colors.BLUE, channel, Colors.NORMAL);
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

                                    using (var db = Database.GetConnection())
                                    {
                                        db.Execute("INSERT INTO `ImportantApps` (`AppID`, `Channel`) VALUES (@AppID, @Channel)", new { AppID = id, Channel = channel });
                                    }
                                        
                                    command.Reply("Marked app {0}{1}{2} ({3}) as important in {4}{5}{6}.", Colors.BLUE, id, Colors.NORMAL, Steam.GetAppName(id), Colors.BLUE, channel, Colors.NORMAL);
                                }

                                return;

                            case "sub":
                                if (Application.ImportantSubs.ContainsKey(id))
                                {
                                    command.Reply("Package {0}{1}{2} ({3}) is already important.", Colors.BLUE, id, Colors.NORMAL, Steam.GetPackageName(id));
                                }
                                else
                                {
                                    Application.ImportantSubs.Add(id, 1);

                                    using (var db = Database.GetConnection())
                                    {
                                        db.Execute("INSERT INTO `ImportantSubs` (`SubID`) VALUES (@SubID)", new { SubID = id });
                                    }

                                    command.Reply("Marked package {0}{1}{2} ({3}) as important.", Colors.BLUE, id, Colors.NORMAL, Steam.GetPackageName(id));
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
                                List<string> channels;

                                if (!Application.ImportantApps.TryGetValue(id, out channels) || !channels.Contains(channel))
                                {
                                    command.Reply("App {0}{1}{2} ({3}) is not important in {4}{5}{6}.", Colors.BLUE, id, Colors.NORMAL, Steam.GetAppName(id), Colors.BLUE, channel, Colors.NORMAL);
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

                                    using (var db = Database.GetConnection())
                                    {
                                        db.Execute("DELETE FROM `ImportantApps` WHERE `AppID` = @AppID AND `Channel` = @Channel", new { AppID = id, Channel = channel });
                                    }

                                    command.Reply("Removed app {0}{1}{2} ({3}) from the important list in {4}{5}{6}.", Colors.BLUE, id, Colors.NORMAL, Steam.GetAppName(id), Colors.BLUE, channel, Colors.NORMAL);
                                }

                                return;

                            case "sub":
                                if (!Application.ImportantSubs.ContainsKey(id))
                                {
                                    command.Reply("Package {0}{1}{2} ({3}) is not important.", Colors.BLUE, id, Colors.NORMAL, Steam.GetPackageName(id));
                                }
                                else
                                {
                                    Application.ImportantSubs.Remove(id);

                                    using (var db = Database.GetConnection())
                                    {
                                        db.Execute("DELETE FROM `ImportantSubs` WHERE `SubID` = @SubID", new { SubID = id });
                                    }
                                        
                                    command.Reply("Removed package {0}{1}{2} ({3}) from the important list.", Colors.BLUE, id, Colors.NORMAL, Steam.GetPackageName(id));
                                }

                                return;
                        }

                        break;

                    case "queue":
                        if (count < 3)
                        {
                            break;
                        }

                        if (!uint.TryParse(s[2], out id))
                        {
                            break;
                        }

                        string name;

                        switch (s[1])
                        {
                            case "app":
                                using (var db = Database.GetConnection())
                                {
                                    name = db.ExecuteScalar<string>("SELECT `Name` FROM `Apps` WHERE `AppID` = @AppID", new { AppID = id });
                                }
                                    
                                if (!string.IsNullOrEmpty(name))
                                {
                                    StoreQueue.AddAppToQueue(id);

                                    command.Reply("App {0}{1}{2} ({3}) has been added to the store update queue.", Colors.BLUE, id, Colors.NORMAL, Utils.RemoveControlCharacters(name));

                                    return;
                                }

                                command.Reply("This app is not in the database.");

                                return;

                            case "sub":
                                if (id == 0)
                                {
                                    command.Reply("Sub 0 can not be queued.");

                                    return;
                                }

                                using (var db = Database.GetConnection())
                                {
                                    name = db.ExecuteScalar<string>("SELECT `Name` FROM `Subs` WHERE `SubID` = @SubID", new { SubID = id });
                                }
                                    
                                if (!string.IsNullOrEmpty(name))
                                {
                                    StoreQueue.AddPackageToQueue(id);

                                    command.Reply("Package {0}{1}{2} ({3}) has been added to the store update queue.", Colors.BLUE, id, Colors.NORMAL, Utils.RemoveControlCharacters(name));

                                    return;
                                }

                                command.Reply("This package is not in the database.");

                                return;
                        }

                        break;
                }
            }

            command.Reply("Usage:{0} important reload {1}or{2} important <add/remove/queue> <app/sub> <id>", Colors.OLIVE, Colors.NORMAL, Colors.OLIVE);
        }
    }
}
