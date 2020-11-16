/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Timers;
using Dapper;
using SteamKit2;

namespace SteamDatabaseBackend
{
    internal class Connection : SteamHandler, IDisposable
    {
        public const uint RETRY_DELAY = 15;

        public static DateTime LastSuccessfulLogin { get; private set; }

        private Timer ReconnectionTimer;

        private string AuthCode;
        private bool IsTwoFactor;

        public Connection(CallbackManager manager)
        {
            ReconnectionTimer = new Timer
            {
                AutoReset = false
            };
            ReconnectionTimer.Elapsed += Reconnect;
            ReconnectionTimer.Interval = TimeSpan.FromSeconds(RETRY_DELAY).TotalMilliseconds;

            manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
            manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
            manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
            manager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
            manager.Subscribe<SteamUser.UpdateMachineAuthCallback>(OnMachineAuth);
            manager.Subscribe<SteamUser.LoginKeyCallback>(OnLoginKey);
        }

        public void Dispose()
        {
            if (ReconnectionTimer != null)
            {
                ReconnectionTimer.Dispose();
                ReconnectionTimer = null;
            }
        }

        public static void Reconnect(object sender, ElapsedEventArgs e)
        {
            Log.WriteDebug(nameof(Steam), "Reconnecting...");

            Steam.Instance.Client.Connect();
        }

        private async void OnConnected(SteamClient.ConnectedCallback callback)
        {
            ReconnectionTimer.Stop();

            await using var db = await Database.GetConnectionAsync();
            var config = (await db.QueryAsync<(string, string)>(
                "SELECT `ConfigKey`, `Value` FROM `LocalConfig` WHERE `ConfigKey` IN ('backend.sentryhash', 'backend.loginkey')"
            )).ToDictionary(x => x.Item1, x => x.Item2);

            Log.WriteInfo(nameof(Steam), "Connected, logging in...");

            if (Settings.Current.Steam.Username == "anonymous")
            {
                Log.WriteInfo(nameof(Steam), "Using an anonymous account");

                Steam.Instance.User.LogOnAnonymous();

                return;
            }

            Steam.Instance.User.LogOn(new SteamUser.LogOnDetails
            {
                Username = Settings.Current.Steam.Username,
                Password = Settings.Current.Steam.Password,
                AuthCode = IsTwoFactor ? null : AuthCode,
                TwoFactorCode = IsTwoFactor ? AuthCode : null,
                ShouldRememberPassword = true,
                SentryFileHash = config.TryGetValue("backend.sentryhash", out var sentryHash) ? Utils.StringToByteArray(sentryHash) : null,
                LoginKey = config.TryGetValue("backend.loginkey", out var loginKey) ? loginKey : null,
                LoginID = 0x78_50_61_77,
            });
            AuthCode = null;
        }

        private void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            Steam.Instance.PICSChanges.StopTick();

            if (!Steam.Instance.IsRunning)
            {
                Log.WriteInfo(nameof(Steam), "Disconnected from Steam");

                return;
            }
            
            Log.WriteInfo(nameof(Steam), $"Disconnected from Steam. Retrying in {RETRY_DELAY} seconds... {(callback.UserInitiated ? " (user initiated)" : "")}");

            ReconnectionTimer.Start();
        }

        private async void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            if (callback.Result == EResult.AccountLogonDenied)
            {
                Console.Write($"STEAM GUARD! Please enter the auth code sent to the email at {callback.EmailDomain}: ");

                IsTwoFactor = false;
                AuthCode = Console.ReadLine()?.Trim();

                return;
            }
            else if (callback.Result == EResult.AccountLoginDeniedNeedTwoFactor)
            {
                Console.Write("STEAM GUARD! Please enter your 2 factor auth code from your authenticator app: ");

                IsTwoFactor = true;
                AuthCode = Console.ReadLine()?.Trim();

                return;
            }

            if (callback.Result == EResult.InvalidPassword)
            {
                await using var db = await Database.GetConnectionAsync();
                await db.ExecuteAsync("DELETE FROM `LocalConfig` WHERE `ConfigKey` = 'backend.loginkey'");
            }

            if (callback.Result != EResult.OK)
            {
                Log.WriteInfo(nameof(Steam), $"Failed to login: {callback.Result}");

                return;
            }

            LastSuccessfulLogin = DateTime.Now;

            Log.WriteInfo(nameof(Steam), $"Logged in, current Valve time is {callback.ServerTime:R}");

            await Steam.Instance.DepotProcessor.UpdateContentServerList();

            JobManager.RestartJobsIfAny();

            if (!Settings.IsFullRun)
            {
                Steam.Instance.PICSChanges.StartTick();
            }
            else if (Steam.Instance.PICSChanges.PreviousChangeNumber == 0)
            {
                Steam.Instance.PICSChanges.PreviousChangeNumber = 1;

                _ = TaskManager.Run(FullUpdateProcessor.PerformSync);
            }
        }

        private void OnLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            Log.WriteInfo(nameof(Steam), $"Logged out of Steam: {callback.Result}");
        }

        private async void OnMachineAuth(SteamUser.UpdateMachineAuthCallback callback)
        {
            Log.WriteInfo(nameof(Steam), $"Updating sentry file... {callback.FileName}");

            if (callback.Data.Length != callback.BytesToWrite)
            {
                ErrorReporter.Notify(nameof(Steam), new InvalidDataException($"Data.Length ({callback.Data.Length}) != BytesToWrite ({callback.BytesToWrite}) in OnMachineAuth"));
            }

            byte[] sentryHash;
            int sentryFileSize;

            await using (var stream = new MemoryStream(callback.BytesToWrite))
            {
                stream.Seek(callback.Offset, SeekOrigin.Begin);
                stream.Write(callback.Data, 0, callback.BytesToWrite);
                stream.Seek(0, SeekOrigin.Begin);
                
                using var sha = SHA1.Create();
                sentryHash = await sha.ComputeHashAsync(stream);
                sentryFileSize = (int)stream.Length;
            }

            Steam.Instance.User.SendMachineAuthResponse(new SteamUser.MachineAuthDetails
            {
                JobID = callback.JobID,

                FileName = callback.FileName,

                BytesWritten = callback.BytesToWrite,
                FileSize = sentryFileSize,
                Offset = callback.Offset,

                Result = EResult.OK,
                LastError = 0,

                OneTimePassword = callback.OneTimePassword,

                SentryFileHash = sentryHash,
            });

            await LocalConfig.Update("backend.sentryhash", Utils.ByteArrayToString(sentryHash));
        }

        private async void OnLoginKey(SteamUser.LoginKeyCallback callback)
        {
            Log.WriteInfo(nameof(Steam), $"Got new login key with unique id {callback.UniqueID}");

            await LocalConfig.Update("backend.loginkey", callback.LoginKey);

            Steam.Instance.User.AcceptNewLoginKey(callback);
        }
    }
}
