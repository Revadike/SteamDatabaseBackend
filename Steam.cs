/*
 * Copyright (c) 2013, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Amib.Threading;
using MySql.Data.MySqlClient;
using SteamKit2;

namespace SteamDatabaseBackend
{
    public class Steam
    {
        private static Steam _instance = new Steam();
        public static Steam Instance { get { return _instance; } }

        public SteamClient Client { get; private set; }
        public SteamUser User { get; private set; }
        public SteamApps Apps { get; private set; }
        public SteamFriends Friends { get; private set; }
        public SteamUserStats UserStats { get; private set; }
        public SteamUnifiedMessages Unified { get; private set; }
        public CallbackManager CallbackManager { get; private set; }
        private GameCoordinator GameCoordinator;

        public uint PreviousChange { get; set; }

        private bool IsFullRun;
        public bool IsRunning { get; set; }

        public System.Timers.Timer Timer { get; private set; }

        public SmartThreadPool ProcessorPool { get; private set; }
        public SmartThreadPool SecondaryPool { get; private set; }

        private string AuthCode;

        public void GetPICSChanges()
        {
            Apps.PICSGetChangesSince(PreviousChange, true, true);
        }

        private void GetLastChangeNumber()
        {
            // If we're in a full run, request all changes from #1
            if (!IsFullRun && Settings.Current.FullRun > 0)
            {
                PreviousChange = 1;

                return;
            }

            using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `ChangeID` FROM `Changelists` ORDER BY `ChangeID` DESC LIMIT 1"))
            {
                if (Reader.Read())
                {
                    PreviousChange = Reader.GetUInt32("ChangeID");

                    Log.WriteInfo("Steam", "Previous changelist was {0}", PreviousChange);
                }
            }

            if (PreviousChange == 0)
            {
                Log.WriteWarn("Steam", "Looks like there are no changelists in the database.");
                Log.WriteWarn("Steam", "If you want to fill up your database first, restart with \"FullRun\" setting set to 1.");
            }
        }

        public void Init()
        {
            ProcessorPool = new SmartThreadPool(new STPStartInfo { WorkItemPriority = WorkItemPriority.Highest, MaxWorkerThreads = 50 });
            SecondaryPool = new SmartThreadPool();

            ProcessorPool.Name = "Processor Pool";
            SecondaryPool.Name = "Secondary Pool";

            Timer = new System.Timers.Timer();
            Timer.Elapsed += OnTimer;
            Timer.Interval = TimeSpan.FromSeconds(1).TotalMilliseconds;

            Client = new SteamClient();

            User = Client.GetHandler<SteamUser>();
            Apps = Client.GetHandler<SteamApps>();
            Friends = Client.GetHandler<SteamFriends>();
            UserStats = Client.GetHandler<SteamUserStats>();
            Unified = Client.GetHandler<SteamUnifiedMessages>();

            CallbackManager = new CallbackManager(Client);

            CallbackManager.Register(new Callback<SteamClient.ConnectedCallback>(OnConnected));
            CallbackManager.Register(new Callback<SteamClient.DisconnectedCallback>(OnDisconnected));

            CallbackManager.Register(new Callback<SteamUser.AccountInfoCallback>(OnAccountInfo));
            CallbackManager.Register(new Callback<SteamUser.LoggedOnCallback>(OnLoggedOn));
            CallbackManager.Register(new Callback<SteamUser.LoggedOffCallback>(OnLoggedOff));

            CallbackManager.Register(new Callback<SteamApps.LicenseListCallback>(OnLicenseListCallback));

            CallbackManager.Register(new JobCallback<SteamUser.UpdateMachineAuthCallback>(OnMachineAuth));
            CallbackManager.Register(new JobCallback<SteamApps.PICSProductInfoCallback>(OnPICSProductInfo));

            // irc specific
            if (Settings.Current.FullRun == 0)
            {
                CallbackManager.Register(new JobCallback<SteamApps.PICSChangesCallback>(OnPICSChanges));
                CallbackManager.Register(new JobCallback<SteamUserStats.NumberOfPlayersCallback>(SteamProxy.Instance.OnNumberOfPlayers));
                CallbackManager.Register(new Callback<SteamFriends.ClanStateCallback>(SteamProxy.Instance.OnClanState));
                CallbackManager.Register(new Callback<SteamFriends.ChatMsgCallback>(SteamProxy.Instance.OnChatMessage));
                CallbackManager.Register(new Callback<SteamFriends.ChatMemberInfoCallback>(SteamProxy.Instance.OnChatMemberInfo));
                CallbackManager.Register(new Callback<SteamUser.MarketingMessageCallback>(MarketingHandler.OnMarketingMessage));

                // game coordinator
                if (Settings.Current.Steam.IdleAppID > 0)
                {
                    GameCoordinator = new GameCoordinator(Settings.Current.Steam.IdleAppID, Client, CallbackManager);
                }
            }
            else
            {
                CallbackManager.Register(new JobCallback<SteamApps.PICSChangesCallback>(OnPICSChangesFullRun));
            }

            DepotProcessor.Init();

            GetLastChangeNumber();

            IsRunning = true;

            Client.Connect();

            while (IsRunning)
            {
                CallbackManager.RunWaitCallbacks(TimeSpan.FromSeconds(1));
            }
        }

        private void OnTimer(object sender, System.Timers.ElapsedEventArgs e)
        {
            GetPICSChanges();
        }

        private void OnConnected(SteamClient.ConnectedCallback callback)
        {
            if (callback.Result != EResult.OK)
            {
                GameCoordinator.UpdateStatus(0, callback.Result.ToString());

                IRC.SendEmoteAnnounce("failed to connect: {0}", callback.Result);

                Log.WriteError("Steam", "Could not connect: {0}", callback.Result);

                IsRunning = false;

                return;
            }

            GameCoordinator.UpdateStatus(0, EResult.NotLoggedOn.ToString());

            Log.WriteInfo("Steam", "Connected, logging in...");

            byte[] sentryHash = null;

            if (File.Exists("sentry.bin"))
            {
                byte[] sentryFile = File.ReadAllBytes("sentry.bin");
                sentryHash = CryptoHelper.SHAHash(sentryFile);
            }
            
            User.LogOn(new SteamUser.LogOnDetails
            {
                Username = Settings.Current.Steam.Username,
                Password = Settings.Current.Steam.Password,

                AuthCode = AuthCode,
                SentryFileHash = sentryHash
            });
        }

        private void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            Timer.Stop();

            if (!IsRunning)
            {
                Log.WriteInfo("Steam", "Disconnected from Steam");
                return;
            }

            GameCoordinator.UpdateStatus(0, EResult.NoConnection.ToString());

            const uint RETRY_DELAY = 15;

            Log.WriteInfo("Steam", "Disconnected from Steam. Retrying in {0} seconds...", RETRY_DELAY);

            IRC.SendEmoteAnnounce("disconnected from Steam. Retrying in {0} seconds...", RETRY_DELAY);

            Thread.Sleep(TimeSpan.FromSeconds(RETRY_DELAY));

            Client.Connect();
        }

        private void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            GameCoordinator.UpdateStatus(0, callback.Result.ToString());

            if (callback.Result == EResult.AccountLogonDenied)
            {
                Console.Write("STEAM GUARD! Please enter the auth code sent to the email at {0}: ", callback.EmailDomain);

                AuthCode = Console.ReadLine().Trim();

                return;
            }

            if (callback.Result != EResult.OK)
            {
                Log.WriteError("Steam", "Failed to login: {0}", callback.Result);

                IRC.SendEmoteAnnounce("failed to log in: {0}", callback.Result);

                Thread.Sleep(TimeSpan.FromSeconds(2));

                return;
            }

            string serverTime = callback.ServerTime.ToString();

            Log.WriteInfo("Steam", "Logged in, current valve time is {0} UTC", serverTime);

            IRC.SendMain("Logged in to Steam. Server time: {0} UTC", serverTime);
            IRC.SendEmoteAnnounce("is now logged in. Server time: {0} UTC", serverTime);

            // Prevent bugs
            if (IsFullRun)
            {
                return;
            }

            if (Settings.Current.FullRun > 0)
            {
                IsFullRun = true;

                GetPICSChanges();
            }
            else
            {
                Timer.Start();

                if (GameCoordinator != null)
                {
                    GameCoordinator.PlayGame();
                }

                foreach (var chatRoom in Settings.Current.ChatRooms)
                {
                    Friends.JoinChat(chatRoom);
                }

#if DEBUG
                Apps.PICSGetProductInfo(440, 61, false, false);
#endif
            }
        }

        private void OnLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            Timer.Stop();

            Log.WriteInfo("Steam", "Logged off of Steam: {0}", callback.Result);

            IRC.SendMain("Logged off of Steam: {0}", callback.Result);
            IRC.SendEmoteAnnounce("logged off of Steam: {0}", callback.Result);

            GameCoordinator.UpdateStatus(0, callback.Result.ToString());
        }

        private void OnMachineAuth(SteamUser.UpdateMachineAuthCallback callback, JobID jobId)
        {
            Log.WriteInfo("Steam", "Updating sentry file...");

            byte[] sentryHash = CryptoHelper.SHAHash(callback.Data);

            File.WriteAllBytes("sentry.bin", callback.Data);

            User.SendMachineAuthResponse(new SteamUser.MachineAuthDetails
            {
                JobID = jobId,

                FileName = callback.FileName,

                BytesWritten = callback.BytesToWrite,
                FileSize = callback.Data.Length,
                Offset = callback.Offset,

                Result = EResult.OK,
                LastError = 0,

                OneTimePassword = callback.OneTimePassword,

                SentryFileHash = sentryHash
            });
        }

        private void OnAccountInfo(SteamUser.AccountInfoCallback callback)
        {
            Friends.SetPersonaState(EPersonaState.Busy);
        }

        private void OnLicenseListCallback(SteamApps.LicenseListCallback licenseList)
        {
            if (licenseList.Result != EResult.OK)
            {
                Log.WriteError("Steam", "Unable to get license list: {0}", licenseList.Result);

                return;
            }

            Log.WriteInfo("Steam", "{0} Licenses: {1}", licenseList.LicenseList.Count, string.Join(", ", licenseList.LicenseList.Select(lic => lic.PackageID)));
        }

        private void OnPICSChangesFullRun(SteamApps.PICSChangesCallback callback, JobID job)
        {
            // Hackiness to prevent processing legit changelists after our request if that ever happens
            if (PreviousChange != 1)
            {
                Log.WriteWarn("Steam", "Got changelist {0}, but ignoring it because we're in a full run", callback.CurrentChangeNumber);

                return;
            }

            PreviousChange = 2;

            Log.WriteInfo("Steam", "Requesting info for {0} apps and {1} packages", callback.AppChanges.Count, callback.PackageChanges.Count);

            Apps.PICSGetProductInfo(callback.AppChanges.Keys, callback.PackageChanges.Keys, false, false);
        }

        private void OnPICSChanges(SteamApps.PICSChangesCallback callback, JobID job)
        {
            if (PreviousChange == callback.CurrentChangeNumber)
            {
                return;
            }

            Log.WriteInfo("Steam", "Changelist {0} -> {1} ({2} apps, {3} packages)", PreviousChange, callback.CurrentChangeNumber, callback.AppChanges.Count, callback.PackageChanges.Count);

            PreviousChange = callback.CurrentChangeNumber;

            DbWorker.ExecuteNonQuery("INSERT INTO `Changelists` (`ChangeID`) VALUES (@ChangeID) ON DUPLICATE KEY UPDATE `Date` = CURRENT_TIMESTAMP()", new MySqlParameter("@ChangeID", callback.CurrentChangeNumber));

            if (callback.AppChanges.Count == 0 && callback.PackageChanges.Count == 0)
            {
                IRC.SendAnnounce("{0}»{1} Changelist {2}{3}{4} (empty)", Colors.RED, Colors.NORMAL, Colors.OLIVE, PreviousChange, Colors.DARK_GRAY);

                return;
            }

            SecondaryPool.QueueWorkItem(SteamProxy.Instance.OnPICSChanges, callback);

            Apps.PICSGetProductInfo(callback.AppChanges.Keys, callback.PackageChanges.Keys, false, false);

            if (callback.AppChanges.Count > 0)
            {
                SecondaryPool.QueueWorkItem(delegate
                {
                    string changes = string.Empty;

                    foreach (var app in callback.AppChanges.Values)
                    {
                        if (callback.CurrentChangeNumber != app.ChangeNumber)
                        {
                            DbWorker.ExecuteNonQuery("INSERT INTO `Changelists` (`ChangeID`) VALUES (@ChangeID) ON DUPLICATE KEY UPDATE `Date` = `Date`", new MySqlParameter("@ChangeID", app.ChangeNumber));
                        }

                        DbWorker.ExecuteNonQuery("UPDATE `Apps` SET `LastUpdated` = CURRENT_TIMESTAMP() WHERE `AppID` = @AppID", new MySqlParameter("@AppID", app.ID));

                        changes += string.Format("({0}, {1}),", app.ChangeNumber, app.ID);
                    }

                    if (!changes.Equals(string.Empty))
                    {
                        changes = string.Format("INSERT INTO `ChangelistsApps` (`ChangeID`, `AppID`) VALUES {0} ON DUPLICATE KEY UPDATE `AppID` = `AppID`", changes.Remove(changes.Length - 1));

                        DbWorker.ExecuteNonQuery(changes);
                    }
                });
            }

            if (callback.PackageChanges.Count > 0)
            {
                SecondaryPool.QueueWorkItem(delegate
                {
                    string changes = string.Empty;

                    foreach (var package in callback.PackageChanges.Values)
                    {
                        if (callback.CurrentChangeNumber != package.ChangeNumber)
                        {
                            DbWorker.ExecuteNonQuery("INSERT INTO `Changelists` (`ChangeID`) VALUES (@ChangeID) ON DUPLICATE KEY UPDATE `Date` = `Date`", new MySqlParameter("@ChangeID", package.ChangeNumber));
                        }

                        DbWorker.ExecuteNonQuery("UPDATE `Subs` SET `LastUpdated` = CURRENT_TIMESTAMP() WHERE `SubID` = @SubID", new MySqlParameter("@SubID", package.ID));

                        changes += string.Format("({0}, {1}),", package.ChangeNumber, package.ID);
                    }

                    if (!changes.Equals(string.Empty))
                    {
                        changes = string.Format("INSERT INTO `ChangelistsSubs` (`ChangeID`, `SubID`) VALUES {0} ON DUPLICATE KEY UPDATE `SubID` = `SubID`", changes.Remove(changes.Length - 1));

                        DbWorker.ExecuteNonQuery(changes);
                    }
                });
            }
        }

        private void OnPICSProductInfo(SteamApps.PICSProductInfoCallback callback, JobID jobID)
        {
            var request = SteamProxy.Instance.IRCRequests.Find(r => r.JobID == jobID);

            if (request != null)
            {
                SteamProxy.Instance.IRCRequests.Remove(request);

                SecondaryPool.QueueWorkItem(SteamProxy.Instance.OnProductInfo, request, callback);

                return;
            }

            foreach (var app in callback.Apps)
            {
                Log.WriteInfo("Steam", "AppID: {0}", app.Key);

                var workaround = app;

                ProcessorPool.QueueWorkItem(delegate
                {
                    new AppProcessor(workaround.Key).Process(workaround.Value);
                });
            }

            foreach (var package in callback.Packages)
            {
                Log.WriteInfo("Steam", "SubID: {0}", package.Key);

                var workaround = package;

                ProcessorPool.QueueWorkItem(delegate
                {
                    new SubProcessor(workaround.Key).Process(workaround.Value);
                });
            }

            // Only handle when fullrun is disabled or if it specifically is running with mode "2" (full run inc. unknown apps)
            if (Settings.Current.FullRun != 1)
            {
                foreach (uint app in callback.UnknownApps)
                {
                    uint workaround = app;

                    ProcessorPool.QueueWorkItem(delegate
                    {
                        new AppProcessor(workaround).ProcessUnknown();
                    });
                }

                foreach (uint package in callback.UnknownPackages)
                {
                    Log.WriteWarn("Steam", "Unknown SubID: {0} - We don't handle these yet", package);
                }
            }
        }
    }
}
