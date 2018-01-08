/*
 * Copyright (c) 2013-2018, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.IO;
using SteamKit2;

namespace SteamDatabaseBackend
{
    public class ReadMachineAuth : ClientMsgHandler
    {
        public override void HandleMsg(IPacketMsg packetMsg)
        {
            if (packetMsg.MsgType == EMsg.ClientServiceModule ||
                packetMsg.MsgType == EMsg.ClientReadMachineAuth ||
                packetMsg.MsgType == EMsg.ClientRequestMachineAuth)
            {
                IRC.Instance.SendOps("Yo xPaw and Netshroud, got {0}", packetMsg.MsgType);

                string file = Path.Combine(Application.Path, "files", ".support", string.Format("dump_{0}_{1}.bin", packetMsg.MsgType, DateTime.UtcNow.ToString("yyyyMMddHHmmssfff")));
                Console.WriteLine(file);
                File.WriteAllBytes(file, packetMsg.GetData());
            }
        }
    }
}
