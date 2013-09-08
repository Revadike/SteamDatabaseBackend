/*
 * Copyright (c) 2013, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using Meebey.SmartIrc4net;
using SteamKit2;

namespace SteamDatabaseBackend
{
    public static class CommandHandler
    {
        public static void OnConnected(object sender, EventArgs e)
        {
            Log.WriteInfo("IRC Proxy", "Connected to IRC successfully");
        }

        public static void OnChannelMessage(object sender, IrcEventArgs e)
        {
            switch (e.Data.MessageArray[0])
            {
                case "!app":
                {
                    uint appid;

                    if (e.Data.MessageArray.Length == 2 && uint.TryParse(e.Data.MessageArray[1], out appid))
                    {
                        var jobID = Program.steam.steamApps.PICSGetProductInfo(appid, null, false, false);

                        Program.ircSteam.IRCRequests.Add(new SteamProxy.IRCRequest
                        {
                            JobID = jobID,
                            Target = appid,
                            Type = SteamProxy.IRCRequestType.TYPE_APP,
                            Channel = e.Data.Channel,
                            Requester = e.Data.Nick
                        });
                    }
                    else
                    {
                        IRC.Send(e.Data.Channel, "Usage:{0} !app <appid>", Colors.OLIVE);
                    }

                    break;
                }

                case "!sub":
                {
                    uint subid;

                    if (e.Data.MessageArray.Length == 2 && uint.TryParse(e.Data.MessageArray[1], out subid))
                    {
                        var jobID = Program.steam.steamApps.PICSGetProductInfo(null, subid, false, false);

                        Program.ircSteam.IRCRequests.Add(new SteamProxy.IRCRequest
                        {
                            JobID = jobID,
                            Target = subid,
                            Type = SteamProxy.IRCRequestType.TYPE_SUB,
                            Channel = e.Data.Channel,
                            Requester = e.Data.Nick
                        });
                    }
                    else
                    {
                        IRC.Send(e.Data.Channel, "Usage:{0} !sub <subid>", Colors.OLIVE);
                    }

                    break;
                }

                case "!numplayers":
                {
                    if (e.Data.MessageArray.Length != 2)
                    {
                        IRC.Send(e.Data.Channel, "Usage:{0} !numplayers <appid>", Colors.OLIVE);

                        break;
                    }

                    uint appid;

                    if (uint.TryParse(e.Data.MessageArray[1], out appid))
                    {
                        var jobID = Program.steam.steamUserStats.GetNumberOfCurrentPlayers(appid);

                        Program.ircSteam.IRCRequests.Add(new SteamProxy.IRCRequest
                        {
                            JobID = jobID,
                            Target = appid,
                            Type = SteamProxy.IRCRequestType.TYPE_PLAYERS,
                            Channel = e.Data.Channel,
                            Requester = e.Data.Nick
                        });
                    }
                    else if (e.Data.MessageArray[1].ToLower().Equals("\x68\x6C\x33"))
                    {
                        IRC.Send(e.Data.Channel, "{0}{1}{2}: People playing {3}{4}{5} right now: {6}{7}", Colors.OLIVE, e.Data.Nick, Colors.NORMAL, Colors.OLIVE, "\x48\x61\x6C\x66\x2D\x4C\x69\x66\x65\x20\x33", Colors.NORMAL, Colors.YELLOW, "\x7e\x34\x30\x30");
                    }

                    break;
                }

                case "!reload":
                {
                    Channel ircChannel = Program.irc.GetChannel(e.Data.Channel);

                    foreach (ChannelUser user in ircChannel.Users.Values)
                    {
                        if (user.IsOp && e.Data.Nick == user.Nick)
                        {
                            Program.ircSteam.ReloadImportant(e.Data.Channel);

                            break;
                        }
                    }

                    break;
                }

                case "!force":
                {
                    Channel ircChannel = Program.irc.GetChannel(e.Data.Channel);

                    foreach (ChannelUser user in ircChannel.Users.Values)
                    {
                        if (user.IsOp && e.Data.Nick == user.Nick)
                        {
                            if (e.Data.MessageArray.Length == 3)
                            {
                                uint target;

                                if (!uint.TryParse(e.Data.MessageArray[2], out target))
                                {
                                    IRC.Send(e.Data.Channel, "Usage:{0} !force [<app/sub> <target>]", Colors.OLIVE);

                                    break;
                                }

                                switch (e.Data.MessageArray[1])
                                {
                                    case "app":
                                    {
                                        Program.steam.steamApps.PICSGetProductInfo(target, null, false, false);

                                        IRC.Send(e.Data.Channel, "Forced update for AppID {0}{1}", Colors.OLIVE, target);

                                        break;
                                    }

                                    case "sub":
                                    {
                                        Program.steam.steamApps.PICSGetProductInfo(null, target, false, false);

                                        IRC.Send(e.Data.Channel, "Forced update for SubID {0}{1}", Colors.OLIVE, target);

                                        break;
                                    }

                                    default:
                                    {
                                        IRC.Send(e.Data.Channel, "Usage:{0} !force [<app/sub> <target>]", Colors.OLIVE);

                                        break;
                                    }
                                }
                            }
                            else if (e.Data.MessageArray.Length == 1)
                            {
                                Program.steam.GetPICSChanges();
                                IRC.Send(e.Data.Channel, "Check forced");
                            }
                            else
                            {
                                IRC.Send(e.Data.Channel, "Usage:{0} !force [<app/sub> <target>]", Colors.OLIVE);
                            }

                            break;
                        }
                    }

                    break;
                }
            }
        }
    }
}
