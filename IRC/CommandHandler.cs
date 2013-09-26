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
        public static void OnChannelMessage(object sender, IrcEventArgs e)
        {
            switch (e.Data.MessageArray[0])
            {
                case "!app":
                {
                    uint appid;

                    if (e.Data.MessageArray.Length == 2 && uint.TryParse(e.Data.MessageArray[1], out appid))
                    {
                        var jobID = Steam.Instance.Apps.PICSGetProductInfo(appid, null, false, false);

                        SteamProxy.Instance.IRCRequests.Add(new SteamProxy.IRCRequest
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
                        var jobID = Steam.Instance.Apps.PICSGetProductInfo(null, subid, false, false);

                        SteamProxy.Instance.IRCRequests.Add(new SteamProxy.IRCRequest
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

#if DEBUG
                case "!depot":
                {
                    uint appid;
                    uint depotid;

                    if (e.Data.MessageArray.Length == 3 && uint.TryParse(e.Data.MessageArray[1], out appid) && uint.TryParse(e.Data.MessageArray[2], out depotid))
                    {
                        var jobID = Steam.Instance.Apps.PICSGetProductInfo(appid, null, false, false);

                        SteamProxy.Instance.IRCRequests.Add(new SteamProxy.IRCRequest
                        {
                            JobID = jobID,
                            Target = appid,
                            DepotID = depotid,
                            Type = SteamProxy.IRCRequestType.TYPE_DEPOT,
                            Channel = e.Data.Channel,
                            Requester = e.Data.Nick
                        });
                    }
                    else
                    {
                        IRC.Send(e.Data.Channel, "Usage:{0} !depot <parent appid> <depotid>", Colors.OLIVE);
                    }

                    break;
                }
#endif

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
                        var jobID = Steam.Instance.UserStats.GetNumberOfCurrentPlayers(appid);

                        SteamProxy.Instance.IRCRequests.Add(new SteamProxy.IRCRequest
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
                    Channel ircChannel = IRC.Instance.Client.GetChannel(e.Data.Channel);

                    foreach (ChannelUser user in ircChannel.Users.Values)
                    {
                        if (user.IsOp && e.Data.Nick == user.Nick)
                        {
                            SteamProxy.Instance.ReloadImportant(e.Data.Channel);

                            break;
                        }
                    }

                    break;
                }

                case "!force":
                {
                    Channel ircChannel = IRC.Instance.Client.GetChannel(e.Data.Channel);

                    foreach (ChannelUser user in ircChannel.Users.Values)
                    {
                        if (user.IsOp && e.Data.Nick == user.Nick)
                        {
                            if (e.Data.MessageArray.Length == 3)
                            {
                                uint target;

                                if (!uint.TryParse(e.Data.MessageArray[2], out target))
                                {
                                    IRC.Send(e.Data.Channel, "Usage:{0} !force [<app/sub/changelist> <target>]", Colors.OLIVE);

                                    break;
                                }

                                switch (e.Data.MessageArray[1])
                                {
                                    case "app":
                                    {
                                        Steam.Instance.Apps.PICSGetProductInfo(target, null, false, false);

                                        IRC.Send(e.Data.Channel, "Forced update for AppID {0}{1}", Colors.OLIVE, target);

                                        break;
                                    }

                                    case "sub":
                                    {
                                        Steam.Instance.Apps.PICSGetProductInfo(null, target, false, false);

                                        IRC.Send(e.Data.Channel, "Forced update for SubID {0}{1}", Colors.OLIVE, target);

                                        break;
                                    }
#if DEBUG
                                    case "changelist":
                                    {
                                        if (Math.Abs(Steam.Instance.PreviousChange - target) > 100)
                                        {
                                            IRC.Send(e.Data.Channel, "Changelist difference is too big, will not execute");

                                            break;
                                        }

                                        Steam.Instance.PreviousChange = target;

                                        Steam.Instance.GetPICSChanges();

                                        IRC.Send(e.Data.Channel, "Requested changes since changelist {0}{1}", Colors.OLIVE, target);

                                        break;
                                    }
#endif
                                    default:
                                    {
                                        IRC.Send(e.Data.Channel, "Usage:{0} !force [<app/sub> <target>]", Colors.OLIVE);

                                        break;
                                    }
                                }
                            }
                            else if (e.Data.MessageArray.Length == 1)
                            {
                                Steam.Instance.GetPICSChanges();
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
