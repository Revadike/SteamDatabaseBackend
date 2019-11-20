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
            Log.WriteDebug("Steam", "Reconnecting...");

            Steam.Instance.Client.Connect();
        }

        private void OnConnected(SteamClient.ConnectedCallback callback)
        {
            ReconnectionTimer.Stop();

            Log.WriteInfo("Steam", "Connected, logging in to cellid {0}...", LocalConfig.Current.CellID);

            if (Settings.Current.Steam.Username == "anonymous")
            {
                Log.WriteInfo("Steam", "Using an anonymous account");

                Steam.Instance.User.LogOnAnonymous(new SteamUser.AnonymousLogOnDetails
                {
                    CellID = LocalConfig.Current.CellID,
                });

                return;
            }

            byte[] sentryHash = null;

            if (LocalConfig.Current.Sentry != null)
            {
                sentryHash = CryptoHelper.SHAHash(LocalConfig.Current.Sentry);
            }

            Steam.Instance.User.LogOn(new SteamUser.LogOnDetails
            {
                Username = Settings.Current.Steam.Username,
                Password = Settings.Current.Steam.Password,
                CellID = LocalConfig.Current.CellID,
                AuthCode = IsTwoFactor ? null : AuthCode,
                TwoFactorCode = IsTwoFactor ? AuthCode : null,
                SentryFileHash = sentryHash,
                ShouldRememberPassword = true,
                LoginKey = LocalConfig.Current.LoginKey,
                LoginID = 0x78_50_61_77,
            });
            AuthCode = null;
        }

        private void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            Steam.Instance.PICSChanges.StopTick();

            if (!Steam.Instance.IsRunning)
            {
                Log.WriteInfo("Steam", "Disconnected from Steam");

                return;
            }
            
            Log.WriteInfo("Steam", "Disconnected from Steam. Retrying in {0} seconds... {1}", RETRY_DELAY, callback.UserInitiated ? " (user initiated)" : "");

            IRC.Instance.SendEmoteAnnounce("disconnected from Steam. Retrying in {0} seconds…", RETRY_DELAY);

            ReconnectionTimer.Start();
        }

        private void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
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

            if (callback.Result == EResult.InvalidPassword)
            {
                LocalConfig.Current.LoginKey = null;
            }

            if (callback.Result != EResult.OK)
            {
                Log.WriteInfo("Steam", "Failed to login: {0}", callback.Result);

                IRC.Instance.SendEmoteAnnounce("failed to log in: {0}", callback.Result);

                return;
            }

            var cellId = callback.CellID;

            if (LocalConfig.Current.CellID != cellId)
            {
                LocalConfig.Current.CellID = cellId;
                LocalConfig.Save();
            }

            LastSuccessfulLogin = DateTime.Now;

            Log.WriteInfo("Steam", "Logged in, current Valve time is {0}", callback.ServerTime.ToString("R"));

            IRC.Instance.SendEmoteAnnounce("logged in. Valve time: {0}", callback.ServerTime.ToString("R"));

            JobManager.RestartJobsIfAny();

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
                Steam.Instance.PICSChanges.StartTick();
            }
        }

        private void OnLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            Log.WriteInfo("Steam", "Logged out of Steam: {0}", callback.Result);

            IRC.Instance.SendEmoteAnnounce("logged out of Steam: {0}", callback.Result);
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

        private void OnLoginKey(SteamUser.LoginKeyCallback callback)
        {
            Log.WriteInfo("Steam", $"Got new login key with unique id {callback.UniqueID}");

            LocalConfig.Current.LoginKey = callback.LoginKey;
            LocalConfig.Save();

            Steam.Instance.User.AcceptNewLoginKey(callback);
        }
    }
}
