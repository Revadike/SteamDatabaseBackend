/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System.Linq;
using System.Threading.Tasks;
using Dapper;
using SteamKit2;
using SteamKit2.Internal;

namespace SteamDatabaseBackend
{
    class KeyCommand : Command
    {
        public KeyCommand()
        {
            Trigger = "key";
            IsSteamCommand = true;
        }

        public override async Task OnCommand(CommandArguments command)
        {
            if (command.CommandType != ECommandType.IRC || command.Recipient != Settings.Current.IRC.Channel.Main)
            {
                command.Reply($"This command is only available in {Settings.Current.IRC.Channel.Main}.");

                return;
            }

            var key = command.Message.Trim();

            if (key.Length < 17)
            {
                command.Reply("Usage:{0} key <steam key>", Colors.OLIVE);

                return;
            }
            
            var msg = new ClientMsgProtobuf<CMsgClientRegisterKey>(EMsg.ClientRegisterKey)
            {
                SourceJobID = Steam.Instance.Client.GetNextJobID(),
                Body =
                {
                    key = key
                }
            };

            Steam.Instance.Client.Send(msg);

            var job = await new AsyncJob<PurchaseResponseCallback>(Steam.Instance.Client, msg.SourceJobID);
            
            if (job.Packages.Count == 0)
            {
                command.Reply($"Nothing has been activated: {Colors.OLIVE}{job.PurchaseResultDetail}");

                return;
            }

            string response;

            switch (job.PurchaseResultDetail)
            {
                case EPurchaseResultDetail.DuplicateActivationCode:
                    response = "This key has already been used by someone else.";
                    break;
                case EPurchaseResultDetail.NoDetail:
                    response = $"{Colors.GREEN}Key activated, thanks ❤️ {Colors.NORMAL}";
                    break;
                case EPurchaseResultDetail.AlreadyPurchased:
                    response = $"{Colors.OLIVE}I already own this.{Colors.NORMAL}";
                    break;
                default:
                    response = $"I don't know what happened. {Colors.OLIVE}{job.PurchaseResultDetail}";
                    break;
            }

            command.Reply($"{response} Packages:{Colors.OLIVE} {string.Join(", ", job.Packages.Select(x => $"{x.Key}: {x.Value}"))}");

            JobManager.AddJob(() => Steam.Instance.Apps.PICSGetProductInfo(Enumerable.Empty<SteamApps.PICSRequest>(), job.Packages.Keys.Select(Utils.NewPICSRequest)));

            using (var db = Database.GetConnection())
            {
                var apps = db.Query<uint>("SELECT `AppID` FROM `SubsApps` WHERE `Type` = \"app\" AND `SubID` IN @Ids", new { Ids = job.Packages.Keys });

                JobManager.AddJob(() => Steam.Instance.Apps.PICSGetAccessTokens(apps, Enumerable.Empty<uint>()));
            }
        }
    }
}
