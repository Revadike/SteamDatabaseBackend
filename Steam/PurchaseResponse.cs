using System;
using System.Collections.Generic;
using System.IO;
using SteamKit2;
using SteamKit2.Internal;

namespace SteamDatabaseBackend
{
    public class PurchaseResponseCallback : CallbackMsg
    {
        public List<uint> Packages { get; } = new List<uint>();

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

                if (packageID > 0)
                {
                    Packages.Add(packageID);
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

            Client.PostCallback(callback);
        }
    }
}
