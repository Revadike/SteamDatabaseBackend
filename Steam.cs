/*
 * Copyright (c) 2013, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections.Concurrent;
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
        public CallbackManager CallbackManager { get; private set; }

        public uint PreviousChange { get; set; }

        public bool IsRunning { get; set; }

        public System.Timers.Timer Timer { get; private set; }

        public SmartThreadPool ProcessorPool { get; private set; }
        public SmartThreadPool SecondaryPool { get; private set; }

        public List<uint> OwnedPackages { get; private set; }
        public List<uint> OwnedApps { get; private set; }

        private ConcurrentDictionary<uint, IWorkItemResult> ProcessedApps { get; set; }
        private ConcurrentDictionary<uint, IWorkItemResult> ProcessedSubs { get; set; }

        private string AuthCode;

        public void GetPICSChanges()
        {
            Apps.PICSGetChangesSince(PreviousChange, true, true);
        }

        private void GetLastChangeNumber()
        {
            // If we're in a full run, request all changes from #1
            if (Settings.IsFullRun)
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

            OwnedPackages = new List<uint>();
            OwnedApps = new List<uint>();

            ProcessedApps = new ConcurrentDictionary<uint, IWorkItemResult>();
            ProcessedSubs = new ConcurrentDictionary<uint, IWorkItemResult>();

            Timer = new System.Timers.Timer();
            Timer.Elapsed += OnTimer;
            Timer.Interval = TimeSpan.FromSeconds(1).TotalMilliseconds;

            Client = new SteamClient();

            User = Client.GetHandler<SteamUser>();
            Apps = Client.GetHandler<SteamApps>();
            Friends = Client.GetHandler<SteamFriends>();
            UserStats = Client.GetHandler<SteamUserStats>();

            CallbackManager = new CallbackManager(Client);

            CallbackManager.Register(new Callback<SteamClient.ConnectedCallback>(OnConnected));
            CallbackManager.Register(new Callback<SteamClient.DisconnectedCallback>(OnDisconnected));

            CallbackManager.Register(new Callback<SteamUser.LoggedOnCallback>(OnLoggedOn));
            CallbackManager.Register(new Callback<SteamUser.LoggedOffCallback>(OnLoggedOff));

            CallbackManager.Register(new Callback<SteamApps.LicenseListCallback>(OnLicenseListCallback));

            CallbackManager.Register(new Callback<SteamUser.UpdateMachineAuthCallback>(OnMachineAuth));
            CallbackManager.Register(new Callback<SteamApps.PICSProductInfoCallback>(OnPICSProductInfo));
            CallbackManager.Register(new Callback<SteamApps.PICSTokensCallback>(OnPICSTokens));

            if (Settings.IsFullRun)
            {
                CallbackManager.Register(new Callback<SteamApps.PICSChangesCallback>(OnPICSChangesFullRun));
            }
            else
            {
                CallbackManager.Register(new Callback<SteamApps.PICSChangesCallback>(OnPICSChanges));
                CallbackManager.Register(new Callback<SteamUserStats.NumberOfPlayersCallback>(SteamProxy.Instance.OnNumberOfPlayers));
                CallbackManager.Register(new Callback<SteamFriends.ClanStateCallback>(SteamProxy.Instance.OnClanState));
                CallbackManager.Register(new Callback<SteamFriends.ChatMsgCallback>(SteamProxy.Instance.OnChatMessage));
                CallbackManager.Register(new Callback<SteamFriends.ChatMemberInfoCallback>(SteamProxy.Instance.OnChatMemberInfo));
                CallbackManager.Register(new Callback<SteamUser.MarketingMessageCallback>(MarketingHandler.OnMarketingMessage));
                CallbackManager.Register(new Callback<SteamUser.AccountInfoCallback>(OnAccountInfo));
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

                Log.WriteInfo("Steam", "Could not connect: {0}", callback.Result);

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
            if (!IsRunning)
            {
                Timer.Stop();

                Log.WriteInfo("Steam", "Disconnected from Steam");

                return;
            }

            if (Timer.Enabled)
            {
                IRC.SendMain("Disconnected from Steam. See{0} http://steamstat.us", Colors.DARK_BLUE);
            }

            Timer.Stop();

            GameCoordinator.UpdateStatus(0, EResult.NoConnection.ToString());

            if (SteamProxy.Instance.IRCRequests.Count > 0)
            {
                foreach (var request in SteamProxy.Instance.IRCRequests)
                {
                    CommandHandler.ReplyToCommand(request.Command, "Your request failed.");
                }

                SteamProxy.Instance.IRCRequests.Clear();
            }

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
                Log.WriteInfo("Steam", "Failed to login: {0}", callback.Result);

                IRC.SendEmoteAnnounce("failed to log in: {0}", callback.Result);

                Thread.Sleep(TimeSpan.FromSeconds(2));

                return;
            }
                
            Log.WriteInfo("Steam", "Logged in, current Valve time is {0}", callback.ServerTime.ToString("R"));

            IRC.SendMain("Logged in to Steam. Valve time: {0}{1}", Colors.DARK_GRAY, callback.ServerTime.ToString("R"));
            IRC.SendEmoteAnnounce("logged in.");

            if (Settings.IsFullRun)
            {
                if (PreviousChange == 1)
                {
                    GetPICSChanges();
                }
            }
            else
            {
                Timer.Start();
            }
        }

        private void OnLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            Timer.Stop();

            Log.WriteInfo("Steam", "Logged out of Steam: {0}", callback.Result);

            IRC.SendMain("Logged out of Steam: {0}{1}{2}. See{3} http://steamstat.us", Colors.OLIVE, callback.Result, Colors.NORMAL, Colors.DARK_BLUE);
            IRC.SendEmoteAnnounce("logged out of Steam: {0}", callback.Result);

            GameCoordinator.UpdateStatus(0, callback.Result.ToString());
        }

        private void OnMachineAuth(SteamUser.UpdateMachineAuthCallback callback)
        {
            Log.WriteInfo("Steam", "Updating sentry file...");

            byte[] sentryHash = CryptoHelper.SHAHash(callback.Data);

            File.WriteAllBytes("sentry.bin", callback.Data);

            User.SendMachineAuthResponse(new SteamUser.MachineAuthDetails
            {
                JobID = callback.JobID,

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

            foreach (var chatRoom in Settings.Current.ChatRooms)
            {
                Friends.JoinChat(chatRoom);
            }
        }

        private void OnLicenseListCallback(SteamApps.LicenseListCallback licenseList)
        {
            if (licenseList.Result != EResult.OK)
            {
                Log.WriteError("Steam", "Unable to get license list: {0}", licenseList.Result);

                return;
            }

            Log.WriteInfo("Steam", "{0} licenses received", licenseList.LicenseList.Count);

            OwnedPackages = licenseList.LicenseList.Select(lic => lic.PackageID).ToList();

            var ownedApps = new List<uint>();

            using (MySqlDataReader Reader = DbWorker.ExecuteReader(string.Format("SELECT DISTINCT `AppID` FROM `SubsApps` WHERE `SubID` IN ({0})", string.Join(", ", OwnedPackages))))
            {
                while (Reader.Read())
                {
                    ownedApps.Add(Reader.GetUInt32("AppID"));
                }
            }

            OwnedApps = ownedApps;
        }

        private void OnPICSChangesFullRun(SteamApps.PICSChangesCallback callback)
        {
            // Hackiness to prevent processing legit changelists after our request if that ever happens
            if (PreviousChange != 1)
            {
                Log.WriteWarn("Steam", "Got changelist {0}, but ignoring it because we're in a full run", callback.CurrentChangeNumber);

                return;
            }

            PreviousChange = 2;

            Log.WriteInfo("Steam", "Requesting info for {0} apps and {1} packages", callback.AppChanges.Count, callback.PackageChanges.Count);

            Apps.PICSGetProductInfo(Enumerable.Empty<SteamApps.PICSRequest>(), callback.PackageChanges.Keys.Select(package => NewPICSRequest(package)));
            Apps.PICSGetAccessTokens(callback.AppChanges.Keys, Enumerable.Empty<uint>());
        }

        private void OnPICSChanges(SteamApps.PICSChangesCallback callback)
        {
            if (PreviousChange == callback.CurrentChangeNumber)
            {
                return;
            }

            if (ProcessorPool.IsIdle)
            {
                Log.WriteDebug("Steam", "Cleaning processed apps and subs");

                ProcessedApps.Clear();
                ProcessedSubs.Clear();
            }

            var packageChangesCount = callback.PackageChanges.Count;
            var appChangesCount = callback.AppChanges.Count;

            Log.WriteInfo("Steam", "Changelist {0} -> {1} ({2} apps, {3} packages)", PreviousChange, callback.CurrentChangeNumber, appChangesCount, packageChangesCount);

            PreviousChange = callback.CurrentChangeNumber;

            DbWorker.ExecuteNonQuery("INSERT INTO `Changelists` (`ChangeID`) VALUES (@ChangeID) ON DUPLICATE KEY UPDATE `Date` = CURRENT_TIMESTAMP()", new MySqlParameter("@ChangeID", callback.CurrentChangeNumber));

            if (appChangesCount == 0 && packageChangesCount == 0)
            {
                IRC.SendAnnounce("{0}»{1} Changelist {2}{3}{4} (empty)", Colors.RED, Colors.NORMAL, Colors.OLIVE, PreviousChange, Colors.DARK_GRAY);

                return;
            }

            SecondaryPool.QueueWorkItem(SteamProxy.Instance.OnPICSChanges, callback);

            // Packages have no tokens so we request info for them right away
            if (packageChangesCount > 0)
            {
                Apps.PICSGetProductInfo(Enumerable.Empty<SteamApps.PICSRequest>(), callback.PackageChanges.Keys.Select(package => NewPICSRequest(package)));
            }

            if (appChangesCount > 0)
            {
                // Get all app tokens
                Apps.PICSGetAccessTokens(callback.AppChanges.Keys, Enumerable.Empty<uint>());

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

            if (packageChangesCount > 0)
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

        private void OnPICSTokens(SteamApps.PICSTokensCallback callback)
        {
            Log.WriteDebug("Steam", "Tokens granted: {0} - Tokens denied: {1}", callback.AppTokens.Count, callback.AppTokensDenied.Count);

            var apps = callback.AppTokensDenied
                .Select(app => NewPICSRequest(app))
                .Concat(callback.AppTokens.Select(app => NewPICSRequest(app.Key, app.Value)));

            var request = SteamProxy.Instance.IRCRequests.Find(r => r.JobID == callback.JobID);

            if (request != null)
            {
                request.JobID = Apps.PICSGetProductInfo(apps, Enumerable.Empty<SteamApps.PICSRequest>());

                return;
            }

            Apps.PICSGetProductInfo(apps, Enumerable.Empty<SteamApps.PICSRequest>());
        }

        private void OnPICSProductInfo(SteamApps.PICSProductInfoCallback callback)
        {
            var request = SteamProxy.Instance.IRCRequests.Find(r => r.JobID == callback.JobID);

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

                IWorkItemResult mostRecentItem;
                ProcessedApps.TryGetValue(workaround.Key, out mostRecentItem);

                var workerItem = ProcessorPool.QueueWorkItem(delegate
                {
                    if (mostRecentItem != null && !mostRecentItem.IsCompleted)
                    {
                        Log.WriteDebug("Steam", "Waiting for app {0} to finish processing", workaround.Key);
                        
                        SmartThreadPool.WaitAll(new IWaitableResult[] { mostRecentItem });
                    }

                    new AppProcessor(workaround.Key).Process(workaround.Value);
                });

                ProcessedApps.AddOrUpdate(app.Key, workerItem, (key, oldValue) => workerItem);
            }

            foreach (var package in callback.Packages)
            {
                Log.WriteInfo("Steam", "SubID: {0}", package.Key);

                var workaround = package;

                IWorkItemResult mostRecentItem;
                ProcessedSubs.TryGetValue(workaround.Key, out mostRecentItem);

                var workerItem = ProcessorPool.QueueWorkItem(delegate
                {
                    if (mostRecentItem != null && !mostRecentItem.IsCompleted)
                    {
                        Log.WriteDebug("Steam", "Waiting for package {0} to finish processing", workaround.Key);

                        SmartThreadPool.WaitAll(new IWaitableResult[] { mostRecentItem });
                    }

                    new SubProcessor(workaround.Key).Process(workaround.Value);
                });

                ProcessedSubs.AddOrUpdate(package.Key, workerItem, (key, oldValue) => workerItem);
            }

            foreach (uint app in callback.UnknownApps)
            {
                Log.WriteInfo("Steam", "Unknown AppID: {0}", app);

                uint workaround = app;

                IWorkItemResult mostRecentItem;
                ProcessedApps.TryGetValue(workaround, out mostRecentItem);

                var workerItem = ProcessorPool.QueueWorkItem(delegate
                {
                    if (mostRecentItem != null && !mostRecentItem.IsCompleted)
                    {
                        Log.WriteDebug("Steam", "Waiting for app {0} to finish processing (unknown)", workaround);

                        SmartThreadPool.WaitAll(new IWaitableResult[] { mostRecentItem });
                    }

                    new AppProcessor(workaround).ProcessUnknown();
                });

                ProcessedApps.AddOrUpdate(app, workerItem, (key, oldValue) => workerItem);
            }

            foreach (uint package in callback.UnknownPackages)
            {
                Log.WriteInfo("Steam", "Unknown SubID: {0}", package);

                uint workaround = package;

                IWorkItemResult mostRecentItem;
                ProcessedSubs.TryGetValue(workaround, out mostRecentItem);

                var workerItem = ProcessorPool.QueueWorkItem(delegate
                {
                    if (mostRecentItem != null && !mostRecentItem.IsCompleted)
                    {
                        Log.WriteDebug("Steam", "Waiting for package {0} to finish processing (unknown)", workaround);

                        SmartThreadPool.WaitAll(new IWaitableResult[] { mostRecentItem });
                    }

                    new SubProcessor(workaround).ProcessUnknown();
                });

                ProcessedSubs.AddOrUpdate(package, workerItem, (key, oldValue) => workerItem);
            }
        }

        private static SteamApps.PICSRequest NewPICSRequest(uint id, ulong accessToken = 0)
        {
            return new SteamApps.PICSRequest(id, accessToken, false);
        }
    }
}
