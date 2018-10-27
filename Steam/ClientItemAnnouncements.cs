/*
 * Copyright (c) 2013-2018, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using SteamKit2;
using SteamKit2.Internal;

namespace SteamDatabaseBackend
{
    public class ClientItemAnnouncements : ClientMsgHandler
    {
        public override void HandleMsg(IPacketMsg packetMsg)
        {
            if (packetMsg.MsgType != EMsg.ClientItemAnnouncements)
            {
                return;
            }

            var response = new ClientMsgProtobuf<CMsgClientItemAnnouncements>(packetMsg);

            Log.WriteInfo("ClientItemAnnouncements", $"New items: {response.Body.count_new_items}");

            IRC.Instance.SendOps($"Got {Colors.GREEN}{response.Body.count_new_items}{Colors.NORMAL} new items");

            TaskManager.RunAsync(async () => await AccountInfo.RefreshAppsToIdle());
        }
    }
}
