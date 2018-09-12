/*
 * Copyright (c) 2013-2018, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Timers;
using SteamKit2;

namespace SteamDatabaseBackend
{
    class Connection : SteamHandler
    {
        public const uint RETRY_DELAY = 15;

        public static DateTime LastSuccessfulLogin;

        public readonly Timer ReconnectionTimer;

        private string AuthCode;
        private bool IsTwoFactor;

        public Connection(CallbackManager manager)
            : base(manager)
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
        }

        public static void Reconnect(object sender, ElapsedEventArgs e)
        {
            if (Steam.Instance.Client.IsConnected)
            {
                Log.WriteDebug("Steam", "Reconnect timer fired, but it's connected already.");

                return;
            }

            Log.WriteDebug("Steam", "Reconnecting...");

            Steam.Instance.Client.Connect();
        }

        private void OnConnected(SteamClient.ConnectedCallback callback)
        {
            GameCoordinator.UpdateStatus(0, EResult.NotLoggedOn.ToString());

            Log.WriteInfo("Steam", "Connected, logging in to cellid {0}...", LocalConfig.CellID);

            byte[] sentryHash = null;

            if (LocalConfig.Sentry != null)
            {
                sentryHash = CryptoHelper.SHAHash(LocalConfig.Sentry);
            }

            Steam.Instance.User.LogOn(new SteamUser.LogOnDetails
            {
                Username = Settings.Current.Steam.Username,
                Password = Settings.Current.Steam.Password,
                CellID = LocalConfig.CellID,
                AuthCode = IsTwoFactor ? null : AuthCode,
                TwoFactorCode = IsTwoFactor ? AuthCode : null,
                SentryFileHash = sentryHash
            });
        }

        private void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            if (!Steam.Instance.IsRunning)
            {
                Application.ChangelistTimer.Stop();

                Log.WriteInfo("Steam", "Disconnected from Steam");

                return;
            }

            Application.ChangelistTimer.Stop();

            GameCoordinator.UpdateStatus(0, EResult.NoConnection.ToString());

            Log.WriteInfo("Steam", "Disconnected from Steam. Retrying in {0} seconds...", RETRY_DELAY);

            IRC.Instance.SendEmoteAnnounce("disconnected from Steam. Retrying in {0} seconds…", RETRY_DELAY);

            ReconnectionTimer.Start();
        }

        private void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            GameCoordinator.UpdateStatus(0, Settings.IsFullRun ? string.Format("Full Run ({0})", Settings.Current.FullRun) : callback.Result.ToString());

            if (callback.Result == EResult.AccountLogonDenied)
            {
                Console.Write("STEAM GUARD! Please enter the auth code sent to the email at {0}: ", callback.EmailDomain);

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

            if (callback.Result != EResult.OK)
            {
                Log.WriteInfo("Steam", "Failed to login: {0}", callback.Result);

                IRC.Instance.SendEmoteAnnounce("failed to log in: {0}", callback.Result);

                return;
            }

            var cellId = callback.CellID;

            if (LocalConfig.CellID != cellId)
            {
                Log.WriteDebug("Local Config", "CellID differs, {0} != {1}, forcing server refetch", LocalConfig.CellID, cellId);

                LocalConfig.Current.CellID = cellId;

                LocalConfig.Save();
            }

            LastSuccessfulLogin = DateTime.Now;

            Log.WriteInfo("Steam", "Logged in, current Valve time is {0}", callback.ServerTime.ToString("R"));

            IRC.Instance.SendEmoteAnnounce("logged in. Valve time: {0}", callback.ServerTime.ToString("R"));

            if (Settings.IsFullRun)
            {
                if (Settings.Current.FullRun == FullRunState.ImportantOnly)
                {
                    JobManager.AddJob(() => Steam.Instance.Apps.PICSGetAccessTokens(Application.ImportantApps.Keys, Enumerable.Empty<uint>()));
                    JobManager.AddJob(() => Steam.Instance.Apps.PICSGetProductInfo(Enumerable.Empty<SteamApps.PICSRequest>(), Application.ImportantSubs.Keys.Select(package => Utils.NewPICSRequest(package))));
                }
                else if (Steam.Instance.PICSChanges.PreviousChangeNumber == 0)
                {
                    Steam.Instance.PICSChanges.PerformSync();
                }
            }
            else
            {
                Application.ChangelistTimer.Start();
            }

            JobManager.RestartJobsIfAny();
        }

        private void OnLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            Application.ChangelistTimer.Stop();

            Log.WriteInfo("Steam", "Logged out of Steam: {0}", callback.Result);

            IRC.Instance.SendEmoteAnnounce("logged out of Steam: {0}", callback.Result);

            GameCoordinator.UpdateStatus(0, callback.Result.ToString());
        }

        private void OnMachineAuth(SteamUser.UpdateMachineAuthCallback callback)
        {
            Log.WriteInfo("Steam", "Updating sentry file... {0}", callback.FileName);

            if (callback.Data.Length != callback.BytesToWrite)
            {
                ErrorReporter.Notify("Steam", new InvalidDataException(string.Format("Data.Length ({0}) != BytesToWrite ({1}) in OnMachineAuth", callback.Data.Length, callback.BytesToWrite)));
            }

            int fileSize;
            byte[] sentryHash;
            
            using (var stream = new MemoryStream(callback.BytesToWrite))
            {
                stream.Seek(callback.Offset, SeekOrigin.Begin);
                stream.Write(callback.Data, 0, callback.BytesToWrite);
                stream.Seek(0, SeekOrigin.Begin);

                fileSize = (int)stream.Length;

                using (var sha = new SHA1CryptoServiceProvider())
                {
                    sentryHash = sha.ComputeHash(stream);
                }

                LocalConfig.Current.Sentry = stream.ToArray();
            }

            LocalConfig.Current.SentryFileName = callback.FileName;
            LocalConfig.Save();

            Steam.Instance.User.SendMachineAuthResponse(new SteamUser.MachineAuthDetails
            {
                JobID = callback.JobID,

                FileName = callback.FileName,

                BytesWritten = callback.BytesToWrite,
                FileSize = fileSize,
                Offset = callback.Offset,

                Result = EResult.OK,
                LastError = 0,

                OneTimePassword = callback.OneTimePassword,

                SentryFileHash = sentryHash
            });
        }
    }
}
