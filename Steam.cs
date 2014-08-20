/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
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
    class Steam
    {
        private static Steam _instance = new Steam();
        public static Steam Instance { get { return _instance; } }

        public SteamClient Client { get; private set; }
        public SteamUser User { get; private set; }
        public SteamApps Apps { get; private set; }
        public SteamFriends Friends { get; private set; }
        public SteamUserStats UserStats { get; private set; }
        public CallbackManager CallbackManager { get; private set; }

        public bool IsRunning { get; set; }

        public System.Timers.Timer Timer { get; private set; }

        public SmartThreadPool ProcessorPool { get; private set; }
        public SmartThreadPool SecondaryPool { get; private set; }

        public Dictionary<uint, byte> OwnedPackages { get; private set; }
        public Dictionary<uint, byte> OwnedApps { get; private set; }

        public Dictionary<uint, byte> ImportantApps { get; set; }
        public Dictionary<uint, byte> ImportantSubs { get; set; }

        public ConcurrentDictionary<uint, IWorkItemResult> ProcessedApps { get; private set; }
        public ConcurrentDictionary<uint, IWorkItemResult> ProcessedSubs { get; private set; }

        private PICSChanges PICSChangesHandler;
        private string AuthCode;

        public void GetPICSChanges()
        {
            Apps.PICSGetChangesSince(PICSChangesHandler.PreviousChangeNumber, true, true);
        }

        public void Init()
        {
            ProcessorPool = new SmartThreadPool(new STPStartInfo { WorkItemPriority = WorkItemPriority.Highest, MaxWorkerThreads = 50 });
            SecondaryPool = new SmartThreadPool();

            ProcessorPool.Name = "Processor Pool";
            SecondaryPool.Name = "Secondary Pool";

            OwnedPackages = new Dictionary<uint, byte>();
            OwnedApps = new Dictionary<uint, byte>();

            ImportantApps = new Dictionary<uint, byte>();
            ImportantSubs = new Dictionary<uint, byte>();

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

            Client.AddHandler(new FreeLicense());

            new ProductInfo();
            new PICSTokens();

            PICSChangesHandler = new PICSChanges();

            if (!Settings.IsFullRun)
            {
                new AccountInfo();
                new MarketingMessage();
                new ClanState();
                new ChatMemberInfo();

                CommandHandler.Init();
            }

            DepotProcessor.Init();

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

        public void ReloadImportant(CommandArguments command = null)
        {
            using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `AppID` FROM `ImportantApps` WHERE `Announce` = 1"))
            {
                ImportantApps.Clear();

                while (Reader.Read())
                {
                    ImportantApps.Add(Reader.GetUInt32("AppID"), 1);
                }
            }

            using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `SubID` FROM `ImportantSubs`"))
            {
                ImportantSubs.Clear();

                while (Reader.Read())
                {
                    ImportantSubs.Add(Reader.GetUInt32("SubID"), 1);
                }
            }

            if (command == null)
            {
                Log.WriteInfo("IRC Proxy", "Loaded {0} important apps and {1} packages", ImportantApps.Count, ImportantSubs.Count);
            }
            else
            {
                CommandHandler.ReplyToCommand(command, "Reloaded {0} important apps and {1} packages", ImportantApps.Count, ImportantSubs.Count);
            }
        }

        public static string GetPackageName(uint subID, bool returnEmptyOnFailure = false)
        {
            using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `Name`, `StoreName` FROM `Subs` WHERE `SubID` = @SubID", new MySqlParameter("SubID", subID)))
            {
                if (Reader.Read())
                {
                    string name = DbWorker.GetString("Name", Reader);

                    if (name.StartsWith("Steam Sub", StringComparison.Ordinal))
                    {
                        string nameStore = DbWorker.GetString("StoreName", Reader);

                        if (!string.IsNullOrEmpty(nameStore))
                        {
                            name = string.Format("{0} {1}({2}){3}", name, Colors.DARKGRAY, nameStore, Colors.NORMAL);
                        }
                    }

                    return name;
                }
            }

            return returnEmptyOnFailure ? string.Empty : string.Format("SubID {0}", subID);
        }

        public static string GetAppName(uint appID, bool returnEmptyOnFailure = false)
        {
            using (MySqlDataReader reader = DbWorker.ExecuteReader("SELECT `Name`, `LastKnownName` FROM `Apps` WHERE `AppID` = @AppID", new MySqlParameter("AppID", appID)))
            {
                if (reader.Read())
                {
                    string name = DbWorker.GetString("Name", reader);
                    string nameLast = DbWorker.GetString("LastKnownName", reader);

                    if (!string.IsNullOrEmpty(nameLast) && !name.Equals(nameLast))
                    {
                        return string.Format("{0} {1}({2}){3}", name, Colors.DARKGRAY, nameLast, Colors.NORMAL);
                    }

                    return name;
                }
            }

            return returnEmptyOnFailure ? string.Empty : string.Format("AppID {0}", appID);
        }

        private void OnConnected(SteamClient.ConnectedCallback callback)
        {
            if (callback.Result != EResult.OK)
            {
                GameCoordinator.UpdateStatus(0, callback.Result.ToString());

                IRC.Instance.SendEmoteAnnounce("failed to connect: {0}", callback.Result);

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
                IRC.Instance.SendMain("Disconnected from Steam. See{0} http://steamstat.us", Colors.DARKBLUE);
            }

            Timer.Stop();

            GameCoordinator.UpdateStatus(0, EResult.NoConnection.ToString());

            JobManager.CancelChatJobsIfAny();

            const uint RETRY_DELAY = 15;

            Log.WriteInfo("Steam", "Disconnected from Steam. Retrying in {0} seconds...", RETRY_DELAY);

            IRC.Instance.SendEmoteAnnounce("disconnected from Steam. Retrying in {0} seconds...", RETRY_DELAY);

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

                IRC.Instance.SendEmoteAnnounce("failed to log in: {0}", callback.Result);

                Thread.Sleep(TimeSpan.FromSeconds(2));

                return;
            }
                
            Log.WriteInfo("Steam", "Logged in, current Valve time is {0}", callback.ServerTime.ToString("R"));

            IRC.Instance.SendMain("Logged in to Steam. Valve time: {0}{1}", Colors.DARKGRAY, callback.ServerTime.ToString("R"));
            IRC.Instance.SendEmoteAnnounce("logged in.");

            if (Settings.IsFullRun)
            {
                if (PICSChangesHandler.PreviousChangeNumber == 1)
                {
                    GetPICSChanges();
                }
            }
            else
            {
                JobManager.RestartJobsIfAny();

                Timer.Start();
            }
        }

        private void OnLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            Timer.Stop();

            Log.WriteInfo("Steam", "Logged out of Steam: {0}", callback.Result);

            IRC.Instance.SendMain("Logged out of Steam: {0}{1}{2}. See{3} http://steamstat.us", Colors.OLIVE, callback.Result, Colors.NORMAL, Colors.DARKBLUE);
            IRC.Instance.SendEmoteAnnounce("logged out of Steam: {0}", callback.Result);

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

        private void OnLicenseListCallback(SteamApps.LicenseListCallback licenseList)
        {
            if (licenseList.Result != EResult.OK)
            {
                Log.WriteError("Steam", "Unable to get license list: {0}", licenseList.Result);

                return;
            }

            Log.WriteInfo("Steam", "{0} licenses received", licenseList.LicenseList.Count);

            OwnedPackages = licenseList.LicenseList.ToDictionary(lic => lic.PackageID, lic => (byte)1);

            OwnedApps.Clear();

            using (MySqlDataReader Reader = DbWorker.ExecuteReader(string.Format("SELECT DISTINCT `AppID` FROM `SubsApps` WHERE `SubID` IN ({0})", string.Join(", ", OwnedPackages.Keys))))
            {
                while (Reader.Read())
                {
                    OwnedApps.Add(Reader.GetUInt32("AppID"), 1);
                }
            }
        }
    }
}
