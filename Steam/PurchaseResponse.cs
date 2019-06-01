/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System.Collections.Generic;
using System.IO;
using SteamKit2;
using SteamKit2.Internal;

namespace SteamDatabaseBackend
{
    public class PurchaseResponseCallback : CallbackMsg
    {
        public Dictionary<uint, string> Packages { get; } = new Dictionary<uint, string>();

        public EPurchaseResultDetail PurchaseResultDetail { get; }
        public EResult Result { get; }

        internal PurchaseResponseCallback(JobID jobID, CMsgClientPurchaseResponse msg)
        {
            JobID = jobID;
            PurchaseResultDetail = (EPurchaseResultDetail)msg.purchase_result_details;
            Result = (EResult)msg.eresult;

            if (msg.purchase_receipt_info == null)
            {
                return;
            }

            var receiptInfo = new KeyValue();
            using (var ms = new MemoryStream(msg.purchase_receipt_info))
            {
                if (!receiptInfo.TryReadAsBinary(ms))
                {
                    return;
                }
            }

            var lineItems = receiptInfo["lineitems"].Children;

            foreach (var lineItem in lineItems)
            {
                var packageID = lineItem["PackageID"].AsUnsignedInteger();
                var name = lineItem["ItemDescription"].Value;

                if (packageID > 0)
                {
                    Packages.Add(packageID, name);
                }
            }
        }
    }

    public class PurchaseResponse : ClientMsgHandler
    {
        public override void HandleMsg(IPacketMsg packetMsg)
        {
            if (packetMsg.MsgType != EMsg.ClientPurchaseResponse)
            {
                return;
            }

            var response = new ClientMsgProtobuf<CMsgClientPurchaseResponse>(packetMsg);
            var callback = new PurchaseResponseCallback(packetMsg.TargetJobID, response.Body);

            Log.WriteInfo("Steam Keys", $"PurchaseResponse (EResult: {callback.Result}, PurchaseResultDetail: {callback.PurchaseResultDetail})");

            Client.PostCallback(callback);
        }
    }
}
