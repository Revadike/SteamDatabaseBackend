/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using SteamKit2;
using SteamKit2.Internal;

namespace SteamDatabaseBackend
{
    class FreeLicenseHandler : ClientMsgHandler
    {
        public JobID RequestFreeLicence(IEnumerable<uint> appIDs)
        {
            var msg = new ClientMsgProtobuf<CMsgClientRequestFreeLicense>(EMsg.ClientRequestFreeLicense);
            msg.Body.appids.AddRange(appIDs);

            var jid = Client.GetNextJobID();
            msg.SourceJobID = jid;

            Client.Send(msg);

            return jid;
        }

        public override void HandleMsg(IPacketMsg packetMsg)
        {
            if (packetMsg.MsgType == EMsg.ClientRequestFreeLicenseResponse)
            {
                HandleClientRequestFreeLicenseResponse(packetMsg);
            }
        }

        void HandleClientRequestFreeLicenseResponse(IPacketMsg packetMsg)
        {
            var resp = new ClientMsgProtobuf<CMsgClientRequestFreeLicenseResponse>(packetMsg);

            JobAction job;
            JobManager.TryRemoveJob(packetMsg.TargetJobID, out job);

            var packageIDs = resp.Body.granted_packageids;
            var appIDs = resp.Body.granted_appids;

            Log.WriteDebug("Free License", "Received free license: {0} ({1} apps, {2} packages)", (EResult)resp.Body.eresult, appIDs.Count, packageIDs.Count);

            if (packageIDs.Count > 0)
            {
                Steam.Instance.Apps.PICSGetProductInfo(Enumerable.Empty<uint>(), packageIDs);

                if (packageIDs.Count > 5)
                {
                    IRC.SendMain("{0}{1}{2} new free licenses granted", Colors.OLIVE, packageIDs.Count, Colors.NORMAL);
                }
                else
                {
                    foreach (var package in packageIDs)
                    {
                        IRC.SendMain("New free license granted: {0}{1}{2} -{3} {4}", Colors.OLIVE, SteamProxy.GetPackageName(package), Colors.NORMAL, Colors.DARK_BLUE, SteamDB.GetPackageURL(package));
                    }
                }
            }

            if (appIDs.Count > 0)
            {
                Steam.Instance.Apps.PICSGetAccessTokens(appIDs, Enumerable.Empty<uint>());
            }
        }
    }
}
