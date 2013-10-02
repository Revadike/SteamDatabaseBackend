/*
 * Copyright (c) 2013, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections.Generic;
using Meebey.SmartIrc4net;
using SteamKit2;

namespace SteamDatabaseBackend
{
    public static class CommandHandler
    {
        private static readonly Dictionary<string, Action<IrcEventArgs>> Commands = new Dictionary<string, Action<IrcEventArgs>>
        {
            { "!help", OnCommandHelp },
            { "!app", OnCommandApp },
            { "!sub", OnCommandPackage },
            { "!package", OnCommandPackage },
            { "!players", OnCommandNumPlayers },
            { "!numplayers", OnCommandNumPlayers },
            { "!reload", OnCommandReload },
            { "!force", OnCommandForce }
        };

        public static void OnChannelMessage(object sender, IrcEventArgs e)
        {
            Action<IrcEventArgs> callbackFunction;

            if (Commands.TryGetValue(e.Data.MessageArray[0], out callbackFunction))
            {
                Log.WriteInfo("IRC", "Handling command {0} for user {1} in channel {2}", e.Data.MessageArray[0], e.Data.Nick, e.Data.Channel);

                callbackFunction(e);
            }
        }

        private static void OnCommandHelp(IrcEventArgs e)
        {
            IRC.Send(e.Data.Channel, "{0}{1}{2}: Available commands: {3}{4}", Colors.OLIVE, e.Data.Nick, Colors.NORMAL, Colors.OLIVE, string.Join(string.Format("{0}, {1}", Colors.NORMAL, Colors.OLIVE), Commands.Keys));
        }

        private static void OnCommandApp(IrcEventArgs e)
        {
            uint appID;

            if (e.Data.MessageArray.Length == 2 && uint.TryParse(e.Data.MessageArray[1], out appID))
            {
                var jobID = Steam.Instance.Apps.PICSGetProductInfo(appID, null, false, false);

                SteamProxy.Instance.IRCRequests.Add(new SteamProxy.IRCRequest
                {
                    JobID = jobID,
                    Target = appID,
                    Type = SteamProxy.IRCRequestType.TYPE_APP,
                    Channel = e.Data.Channel,
                    Requester = e.Data.Nick
                });
            }
            else
            {
                IRC.Send(e.Data.Channel, "Usage:{0} !app <appid>", Colors.OLIVE);
            }
        }

        private static void OnCommandPackage(IrcEventArgs e)
        {
            uint subID;

            if (e.Data.MessageArray.Length == 2 && uint.TryParse(e.Data.MessageArray[1], out subID))
            {
                var jobID = Steam.Instance.Apps.PICSGetProductInfo(null, subID, false, false);

                SteamProxy.Instance.IRCRequests.Add(new SteamProxy.IRCRequest
                {
                    JobID = jobID,
                    Target = subID,
                    Type = SteamProxy.IRCRequestType.TYPE_SUB,
                    Channel = e.Data.Channel,
                    Requester = e.Data.Nick
                });
            }
            else
            {
                IRC.Send(e.Data.Channel, "Usage:{0} !sub <subid>", Colors.OLIVE);
            }
        }

        private static void OnCommandNumPlayers(IrcEventArgs e)
        {
            if (e.Data.MessageArray.Length != 2)
            {
                IRC.Send(e.Data.Channel, "Usage:{0} !numplayers <appid>", Colors.OLIVE);

                return;
            }

            uint appID;

            if (uint.TryParse(e.Data.MessageArray[1], out appID))
            {
                var jobID = Steam.Instance.UserStats.GetNumberOfCurrentPlayers(appID);

                SteamProxy.Instance.IRCRequests.Add(new SteamProxy.IRCRequest
                {
                    JobID = jobID,
                    Target = appID,
                    Type = SteamProxy.IRCRequestType.TYPE_PLAYERS,
                    Channel = e.Data.Channel,
                    Requester = e.Data.Nick
                });
            }
            else if (e.Data.MessageArray[1].ToLower().Equals("\x68\x6C\x33"))
            {
                IRC.Send(e.Data.Channel, "{0}{1}{2}: People playing {3}{4}{5} right now: {6}{7}", Colors.OLIVE, e.Data.Nick, Colors.NORMAL, Colors.OLIVE, "\x48\x61\x6C\x66\x2D\x4C\x69\x66\x65\x20\x33", Colors.NORMAL, Colors.YELLOW, "\x7e\x34\x30\x30");
            }
            else
            {
                IRC.Send(e.Data.Channel, "Usage:{0} !numplayers <appid>", Colors.OLIVE);
            }
        }

        private static void OnCommandReload(IrcEventArgs e)
        {
            if (IRC.IsSenderOp(e))
            {
                SteamProxy.Instance.ReloadImportant(e.Data.Channel, e.Data.Nick);
            }
        }

        private static void OnCommandForce(IrcEventArgs e)
        {
            if (!IRC.IsSenderOp(e))
            {
                return;
            }

            if (e.Data.MessageArray.Length == 3)
            {
                uint target;

                if (!uint.TryParse(e.Data.MessageArray[2], out target))
                {
                    IRC.Send(e.Data.Channel, "Usage:{0} !force [<app/sub/changelist> <target>]", Colors.OLIVE);

                    return;
                }

                switch (e.Data.MessageArray[1])
                {
                    case "app":
                    {
                        Steam.Instance.Apps.PICSGetProductInfo(target, null, false, false);

                        IRC.Send(e.Data.Channel, "{0}{1}{2}: Forced update for AppID {3}{4}", Colors.OLIVE, e.Data.Nick, Colors.NORMAL, Colors.OLIVE, target);

                        break;
                    }

                    case "sub":
                    {
                        Steam.Instance.Apps.PICSGetProductInfo(null, target, false, false);

                        IRC.Send(e.Data.Channel, "{0}{1}{2}: Forced update for SubID {3}{4}", Colors.OLIVE, e.Data.Nick, Colors.NORMAL, Colors.OLIVE, target);

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

                        IRC.Send(e.Data.Channel, "{0}{1}{2}: Requested changes since changelist {3}{4}", Colors.OLIVE, e.Data.Nick, Colors.NORMAL, Colors.OLIVE, target);

                        break;
                    }
#endif

                    default:
                    {
                        IRC.Send(e.Data.Channel, "Usage:{0} !force [<app/sub/changelist> <target>]", Colors.OLIVE);

                        break;
                    }
                }
            }
            else if (e.Data.MessageArray.Length == 1)
            {
                Steam.Instance.GetPICSChanges();

                IRC.Send(e.Data.Channel, "{0}{1}{2}: Forced a check", Colors.OLIVE, e.Data.Nick, Colors.NORMAL);
            }
            else
            {
                IRC.Send(e.Data.Channel, "Usage:{0} !force [<app/sub/changelist> <target>]", Colors.OLIVE);
            }
        }
    }
}
