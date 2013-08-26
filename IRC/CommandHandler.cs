/*
 * Copyright (c) 2013, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Meebey.SmartIrc4net;
using MySql.Data.MySqlClient;
using SteamKit2;

namespace PICSUpdater
{
    class CommandHandler
    {
        public static void Send( string channel, string format, params object[] args )
        {
            Program.irc.SendMessage( SendType.Message, channel, string.Format( format, args ) );
        }

        public static void SendEmote( string channel, string format, params object[] args )
        {
            Program.irc.SendMessage( SendType.Action, channel, string.Format( format, args ) );
        }

        public static void OnChannelMessage(object sender, IrcEventArgs e)
        {
            switch (e.Data.MessageArray[0])
            {
                case "!app":
                {
                    uint appid;

                    if (e.Data.MessageArray.Length == 2 && uint.TryParse(e.Data.MessageArray[1].ToString(), out appid))
                    {
                        //Steam.DumpApp(appid);
                        Send(e.Data.Channel, "{0}Not yet implemented :(", Colors.RED);
                    }
                    else
                    {
                        Send(e.Data.Channel, "Usage:{0} !app <appid>", Colors.OLIVE);
                    }

                    break;
                }
                case "!sub":
                {
                    uint subid;

                    if (e.Data.MessageArray.Length == 2 && uint.TryParse(e.Data.MessageArray[1].ToString(), out subid))
                    {
                        //Steam.DumpSub(subid);
                        Send(e.Data.Channel, "{0}Not yet implemented :(", Colors.RED);
                    }
                    else
                    {
                        Send(e.Data.Channel, "Usage:{0} !sub <subid>", Colors.OLIVE);
                    }

                    break;
                }
                case "!numplayers":
                {
                    uint targetapp;

                    if (e.Data.MessageArray.Length == 2 && uint.TryParse(e.Data.MessageArray[1].ToString(), out targetapp))
                    {
                        //Steam.getNumPlayers(targetapp);
                        Send(e.Data.Channel, "{0}Not yet implemented :(", Colors.RED);
                    }
                    else
                    {
                        Send(e.Data.Channel, "Usage:{0} !numplayers <appid>", Colors.OLIVE);
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
                            Program.steam.ircSteam.ReloadImportant(e.Data.Channel);

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
                                    Send(e.Data.Channel, "Usage:{0} !force [<app/sub> <target>]", Colors.OLIVE);

                                    break;
                                }

                                switch (e.Data.MessageArray[1])
                                {
                                    case "app":
                                    {
                                        Program.steam.steamApps.PICSGetProductInfo(target, null, false, false);

                                        Send(e.Data.Channel, "Forced update for AppID {0}{1}", Colors.OLIVE, target);

                                        break;
                                    }
                                    case "sub":
                                    {
                                        Program.steam.steamApps.PICSGetProductInfo(null, target, false, false);

                                        Send(e.Data.Channel, "Forced update for SubID {0}{1}", Colors.OLIVE, target);

                                        break;
                                    }
                                    default:
                                    {
                                        Send(e.Data.Channel, "Usage:{0} !force [<app/sub> <target>]", Colors.OLIVE);

                                        break;
                                    }
                                }
                            }
                            else if (e.Data.MessageArray.Length == 1)
                            {
                                Program.steam.GetPICSChanges();
                                Send(e.Data.Channel, "Check forced");
                            }
                            else
                            {
                                Send(e.Data.Channel, "Usage:{0} !force [<app/sub> <target>]", Colors.OLIVE);
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
