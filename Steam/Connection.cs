/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
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
        private const uint RETRY_DELAY = 15;

        public static DateTime LastSuccessfulLogin;

        public readonly Timer ReconnectionTimer;

        private readonly string SentryFile;
        private string AuthCode;

        public Connection(CallbackManager manager)
            : base(manager)
        {
            SentryFile = Path.Combine(Application.Path, "files", ".support", "sentry.bin");

            ReconnectionTimer = new Timer();
            ReconnectionTimer.AutoReset = false;
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
            if (callback.Result != EResult.OK)
            {
                GameCoordinator.UpdateStatus(0, callback.Result.ToString());

                IRC.Instance.SendEmoteAnnounce("failed to connect: {0}", callback.Result);

                Log.WriteInfo("Steam", "Could not connect: {0}", callback.Result);

                return;
            }

            GameCoordinator.UpdateStatus(0, EResult.NotLoggedOn.ToString());

            Log.WriteInfo("Steam", "Connected, logging in...");

            byte[] sentryHash = null;

            if (File.Exists(SentryFile))
            {
                sentryHash = CryptoHelper.SHAHash(File.ReadAllBytes(SentryFile));
            }

            Steam.Instance.User.LogOn(new SteamUser.LogOnDetails
            {
                Username = Settings.Current.Steam.Username,
                Password = Settings.Current.Steam.Password,

                AuthCode = AuthCode,
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

            JobManager.CancelChatJobsIfAny();

            Log.WriteInfo("Steam", "Disconnected from Steam. Retrying in {0} seconds...", RETRY_DELAY);

            IRC.Instance.SendEmoteAnnounce("disconnected from Steam. Retrying in {0} seconds…", RETRY_DELAY);

            ReconnectionTimer.Start();
        }

        private void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            GameCoordinator.UpdateStatus(0, callback.Result.ToString());

            if (callback.Result == EResult.AccountLogonDenied)
            {
                Console.Write("STEAM GUARD! Please enter the auth code sent to the email at {0}: ", callback.EmailDomain);

                AuthCode = Console.ReadLine();

                if (AuthCode != null)
                {
                    AuthCode = AuthCode.Trim();
                }

                return;
            }

            if (callback.Result != EResult.OK)
            {
                Log.WriteInfo("Steam", "Failed to login: {0}", callback.Result);

                IRC.Instance.SendEmoteAnnounce("failed to log in: {0}", callback.Result);

                return;
            }

            LastSuccessfulLogin = DateTime.Now;

            Log.WriteInfo("Steam", "Logged in, current Valve time is {0}", callback.ServerTime.ToString("R"));

            IRC.Instance.SendEmoteAnnounce("logged in. Valve time: {0}", callback.ServerTime.ToString("R"));

            if (Settings.IsFullRun)
            {
                if (Settings.Current.FullRun == 3)
                {
                    Steam.Instance.Apps.PICSGetAccessTokens(Application.ImportantApps.Keys, Enumerable.Empty<uint>());
                    Steam.Instance.Apps.PICSGetProductInfo(Enumerable.Empty<SteamApps.PICSRequest>(), Application.ImportantSubs.Keys.Select(package => Utils.NewPICSRequest(package)));
                }
                else if (Steam.Instance.PICSChanges.PreviousChangeNumber == 1)
                {
                    Steam.Instance.Apps.PICSGetChangesSince(1, true, true);
                }
            }
            else
            {
                JobManager.RestartJobsIfAny();

                Application.ChangelistTimer.Start();
            }
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
                ErrorReporter.Notify(new InvalidDataException(string.Format("Data.Length ({0}) != BytesToWrite ({1}) in OnMachineAuth", callback.Data.Length, callback.BytesToWrite)));
            }

            int fileSize;
            byte[] sentryHash;

            using (var file = File.Open(SentryFile, FileMode.OpenOrCreate, FileAccess.ReadWrite))
            {
                file.Seek(callback.Offset, SeekOrigin.Begin);
                file.Write(callback.Data, 0, callback.BytesToWrite);
                file.Seek(0, SeekOrigin.Begin);

                fileSize = (int)file.Length;

                using (var sha = new SHA1CryptoServiceProvider())
                {
                    sentryHash = sha.ComputeHash(file);
                }
            }

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
