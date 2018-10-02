/*
 * Copyright (c) 2013-2018, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Timers;
using Dapper;
using SteamKit2;
using SteamKit2.Internal;

namespace SteamDatabaseBackend
{
    class KeyCommand : Command
    {
        private static Timer ActivationTimer;

        public KeyCommand()
        {
            Trigger = "key";
            IsSteamCommand = true;

            if (Settings.IsFullRun)
            {
                return;
            }

            ActivationTimer = new Timer(TimeSpan.FromMinutes(30).TotalMilliseconds);
            ActivationTimer.Elapsed += OnTimer;
            ActivationTimer.Start();
        }
        
        private async void OnTimer(object sender, ElapsedEventArgs e)
        {
            if (!Steam.Instance.Client.IsConnected)
            {
                return;
            }

            List<string> keys;

            using (var db = Database.Get())
            {
                keys = (await db.QueryAsync<string>($"SELECT `SteamKey` FROM `SteamKeys` WHERE `Result` IN (-1,{(int)EPurchaseResultDetail.RateLimited}) ORDER BY `ID` ASC LIMIT 25")).ToList();
            }

            if (keys.Count == 0)
            {
                return;
            }

            var failuresAllowed = 5;

            foreach (var key in keys)
            {
                var result = await ActivateKey(key);

                if (result == EPurchaseResultDetail.RateLimited)
                {
                    break;
                }

                if (result != EPurchaseResultDetail.NoDetail && --failuresAllowed == 0)
                {
                    break;
                }
            }

            // Restart timer as rate limit runs from first activation
            ActivationTimer.Stop();
            ActivationTimer.Start();
        }

        public override async Task OnCommand(CommandArguments command)
        {
            if (!command.IsUserAdmin)
            {
                command.Reply("Use https://steamdb.info/keys/");

                return;
            }

            var key = command.Message.Trim().ToUpper();

            if (key.Length < 17)
            {
                command.Reply("Usage:{0} key <steam key>", Colors.OLIVE);

                return;
            }

            var result = await ActivateKey(key);

            command.Reply(result.ToString());
        }

        private async Task<EPurchaseResultDetail> ActivateKey(string key)
        {
            var msg = new ClientMsgProtobuf<CMsgClientRegisterKey>(EMsg.ClientRegisterKey)
            {
                SourceJobID = Steam.Instance.Client.GetNextJobID(),
                Body =
                {
                    key = key
                }
            };

            Steam.Instance.Client.Send(msg);

            PurchaseResponseCallback job;

            try
            {
                job = await new AsyncJob<PurchaseResponseCallback>(Steam.Instance.Client, msg.SourceJobID);
            }
            catch (TaskCanceledException)
            {
                return EPurchaseResultDetail.Timeout;
            }

            using (var db = Database.Get())
            using (var sha = new SHA1CryptoServiceProvider())
            {
                await db.ExecuteAsync("UPDATE `SteamKeys` SET `SteamKey` = @HashedKey, `SubID` = @SubID, `Result` = @PurchaseResultDetail WHERE `SteamKey` = @SteamKey OR `SteamKey` = @HashedKey",
                    new
                    {
                        job.PurchaseResultDetail,
                        SubID = job.Packages.Count > 0 ? (int)job.Packages.First().Key : -1,
                        SteamKey = key,
                        HashedKey = Utils.ByteArrayToString(sha.ComputeHash(Encoding.ASCII.GetBytes(key)))
                    });
            }

            if (job.Packages.Count == 0)
            {
                IRC.Instance.SendOps($"{Colors.GREEN}[Keys]{Colors.NORMAL} Key not activated:{Colors.OLIVE} {job.Result} - {job.PurchaseResultDetail}");

                return job.PurchaseResultDetail;
            }

            if (job.PurchaseResultDetail != EPurchaseResultDetail.AlreadyPurchased
            &&  job.PurchaseResultDetail != EPurchaseResultDetail.DuplicateActivationCode
            &&  job.PurchaseResultDetail != EPurchaseResultDetail.DoesNotOwnRequiredApp)
            {
                var response = job.PurchaseResultDetail == EPurchaseResultDetail.NoDetail ?
                    $"{Colors.GREEN}Key activated" : $"{Colors.BLUE}{job.PurchaseResultDetail}";

                IRC.Instance.SendOps($"{Colors.GREEN}[Keys]{Colors.NORMAL} {response}{Colors.NORMAL}. Packages:{Colors.OLIVE} {string.Join(", ", job.Packages.Select(x => $"{x.Key}: {x.Value}"))}");
            }

            JobManager.AddJob(() => Steam.Instance.Apps.PICSGetProductInfo(Enumerable.Empty<SteamApps.PICSRequest>(), job.Packages.Keys.Select(Utils.NewPICSRequest)));

            using (var db = await Database.GetConnectionAsync())
            {
                var apps = await db.QueryAsync<uint>("SELECT `AppID` FROM `SubsApps` WHERE `Type` = \"app\" AND `SubID` IN @Ids", new { Ids = job.Packages.Keys });

                JobManager.AddJob(() => Steam.Instance.Apps.PICSGetAccessTokens(apps, Enumerable.Empty<uint>()));

                foreach (var package in job.Packages)
                {
                    var databaseName = (await db.QueryAsync<string>("SELECT `LastKnownName` FROM `Subs` WHERE `SubID` = @SubID", new { SubID = package.Key })).FirstOrDefault() ?? string.Empty;

                    if (databaseName.Equals(package.Value, StringComparison.CurrentCultureIgnoreCase))
                    {
                        continue;
                    }

                    await db.ExecuteAsync("UPDATE `Subs` SET `LastKnownName` = @Name WHERE `SubID` = @SubID", new { SubID = package.Key, Name = package.Value });

                    await db.ExecuteAsync(SubProcessor.HistoryQuery,
                        new PICSHistory
                        {
                            ID = package.Key,
                            Key = SteamDB.DATABASE_NAME_TYPE,
                            OldValue = "key activation",
                            NewValue = package.Value,
                            Action = "created_info"
                        }
                    );
                }
            }

            return job.PurchaseResultDetail;
        }
    }
}
