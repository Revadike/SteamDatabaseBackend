/*
 * Copyright (c) 2013, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MySql.Data.MySqlClient;
using SteamKit2;
using System.Linq;

namespace PICSUpdater
{
    class Steam
    {
        public SteamClient steamClient;
        public SteamUser steamUser;
        public SteamApps steamApps;
        public SteamFriends steamFriends;
        public SteamUserStats steamUserStats;

        public CallbackManager manager;

        private uint PreviousChange = 0;
        private AppProcessor AppPro = new AppProcessor();
        private SubProcessor SubPro = new SubProcessor();

        public uint fullRunOption;
        private bool fullRun = false;
        public bool isRunning = true;

        public SteamProxy ircSteam;

        public System.Timers.Timer timer;

        public void GetPICSChanges()
        {
            steamApps.PICSGetChangesSince(PreviousChange, true, true);
        }

        private void GetLastChangeNumber()
        {
            using (MySqlDataReader Reader = DbWorker.ExecuteReader(@"SELECT `ChangeID` FROM `Changelists` ORDER BY `ChangeID` DESC LIMIT 1"))
            {
                if (Reader.Read())
                {
                    PreviousChange = Reader.GetUInt32("ChangeID");

                    Console.WriteLine("Previous changelist was {0}", PreviousChange);
                }

                Reader.Close();
                Reader.Dispose();
            }
        }

        public void Run()
        {
            steamClient = new SteamClient();
            steamUser = steamClient.GetHandler<SteamUser>();
            steamApps = steamClient.GetHandler<SteamApps>();
            steamFriends = steamClient.GetHandler<SteamFriends>();
            steamUserStats = steamClient.GetHandler<SteamUserStats>();

            manager = new CallbackManager(steamClient);

            ircSteam = new SteamProxy();

            uint.TryParse(ConfigurationManager.AppSettings["fullrun"], out fullRunOption);

            if (fullRunOption == 0)
            {
                DebugLog.AddListener(( category, msg ) => Console.WriteLine("[SteamKit] {0}: {1}", category, msg));
            }

            timer = new System.Timers.Timer();
            timer.Elapsed += new System.Timers.ElapsedEventHandler(OnTimer);
            timer.Interval = 1000;

            new Callback<SteamClient.ConnectedCallback>(OnConnected, manager);
            new Callback<SteamClient.DisconnectedCallback>(OnDisconnected, manager);

            new Callback<SteamUser.AccountInfoCallback>(OnAccountInfo, manager);
            new Callback<SteamUser.LoggedOnCallback>(OnLoggedOn, manager);

            new JobCallback<SteamApps.PICSChangesCallback>(OnPICSChanges, manager);
            new JobCallback<SteamApps.PICSProductInfoCallback>(OnPICSProductInfo, manager);

            GetLastChangeNumber();

            steamClient.Connect();

            while (isRunning == true)
            {
                manager.RunWaitCallbacks(TimeSpan.FromSeconds(1));
            }
        }

        private void OnTimer(object sender, System.Timers.ElapsedEventArgs e)
        {
            //Console.WriteLine(DateTime.Now.ToString("o"));

            GetPICSChanges();
        }

        private void OnConnected(SteamClient.ConnectedCallback callback)
        {
            if (callback.Result != EResult.OK)
            {
                CommandHandler.SendEmote(Program.channelAnnounce, "failed to connect: {0}", callback.Result.ToString());

                throw new Exception("Could not connect: " + callback.Result);
            }

            Console.WriteLine("Connected! Logging in...");

            steamUser.LogOn(new SteamUser.LogOnDetails
            {
                Username = ConfigurationManager.AppSettings["steam-username"],
                Password = ConfigurationManager.AppSettings["steam-password"]
            });
        }

        private void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            timer.Stop();

            if (!isRunning)
            {
                Console.WriteLine("Disconnected from Steam");
                return;
            }

            Console.WriteLine("Disconnected from Steam. Retrying in 15 seconds...");
            Thread.Sleep(TimeSpan.FromSeconds(15));
            steamClient.Connect();
        }

        private void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            if (callback.Result != EResult.OK)
            {
                Console.WriteLine("Failed to login: {0}", callback.Result);

                CommandHandler.SendEmote(Program.channelAnnounce, "failed to log in: {0}", callback.Result.ToString());

                Thread.Sleep(TimeSpan.FromSeconds(2));

                return;
            }

            Console.WriteLine("Logged in");

            CommandHandler.SendEmote(Program.channelAnnounce, "is now logged in.");

            // Prevent bugs
            if (fullRun)
            {
                return;
            }

            if (fullRunOption > 0)
            {
                fullRun = true;

                Console.WriteLine("Running full update with option \"{0}\"", fullRunOption);

                uint i = 0;
                List<uint> appsList = new List<uint>();
                List<uint> packagesList = new List<uint>();

                for (i = 0; i <= 300000; i++)
                {
                    appsList.Add(i);
                }

                using (dynamic steamAppsAPI = WebAPI.GetInterface("ISteamApps"))
                {
                    KeyValue kvApps = steamAppsAPI.GetAppList();
                    List<uint> appsListAPI = new List<uint>();

                    // TODO: Make this look nicer
                    foreach (KeyValue app in kvApps[ "apps" ][ "app" ].Children)
                    {
                        appsListAPI.Add((uint)app["appid"].AsInteger());
                    }

                    appsList = appsList.Union(appsListAPI).ToList();
                }

                if (fullRunOption == 1)
                {
                    for (i = 0; i <= 50000; i++)
                    {
                        packagesList.Add(i);
                    }
                }

                Console.WriteLine("Requesting {0} apps and {1} packages", appsList.Count, packagesList.Count);

                CommandHandler.Send(Program.channelAnnounce, "Running a full run. Requesting {0} apps and {1} packages {2}(option: {3})", appsList.Count, packagesList.Count, Colors.DARK_GRAY, fullRunOption);

                steamApps.PICSGetProductInfo(appsList, packagesList, false, false);
            }
            else
            {
                timer.Start();

                ircSteam.PlayGame(440);
            }
        }

        private void OnLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            Console.WriteLine("Logged off of Steam");

            CommandHandler.SendEmote(Program.channelAnnounce, "logged off of Steam.");
        }

        private void OnAccountInfo(SteamUser.AccountInfoCallback callback)
        {
            steamFriends.SetPersonaState(EPersonaState.Busy);
        }

        private void OnPICSChanges(SteamApps.PICSChangesCallback callback, JobID job)
        {
            if (fullRun)
            {
                Console.WriteLine("Received changedlist while processing a full run, ignoring.");
                return;
            }
            else if (PreviousChange == 0)
            {
                Console.WriteLine("PreviousChange was 0. Rolling back by one changelist.");
                PreviousChange = callback.CurrentChangeNumber - 1;
                return;
            }
            else if (PreviousChange != callback.CurrentChangeNumber)
            {
                Console.WriteLine("Got changelist {0}, previous is {1}", callback.CurrentChangeNumber, PreviousChange);

                List<uint> appslist = new List<uint>();
                List<uint> packageslist = new List<uint>();

                Dictionary<uint, uint> appslistIRC = new Dictionary<uint, uint>();
                Dictionary<uint, uint> packageslistIRC = new Dictionary<uint, uint>();

                DbWorker.ExecuteNonQuery("INSERT INTO Changelists (ChangeID) VALUES (@ChangeID) ON DUPLICATE KEY UPDATE date = NOW()",
                new MySqlParameter[]
                                    {
                                        new MySqlParameter("@ChangeID", callback.CurrentChangeNumber)
                                    });

                foreach (var callbackapp in callback.AppChanges)
                {
                    appslist.Add(callbackapp.Key);
                    appslistIRC.Add(callbackapp.Value.ID, callbackapp.Value.ChangeNumber);

                    DbWorker.ExecuteNonQuery("UPDATE Apps SET LastUpdated = CURRENT_TIMESTAMP WHERE AppID = @AppID",
                    new MySqlParameter[]
                            {
                                new MySqlParameter("@AppID", callbackapp.Key)
                            });
                    if (!callback.CurrentChangeNumber.Equals(callbackapp.Value.ChangeNumber))
                    {
                        DbWorker.ExecuteNonQuery("INSERT IGNORE INTO Changelists (ChangeID) VALUES (@ChangeID)",
                        new MySqlParameter[]
                                    {
                                        new MySqlParameter("@ChangeID", callbackapp.Value.ChangeNumber)
                                    });
                    }
                    DbWorker.ExecuteNonQuery("INSERT IGNORE INTO ChangelistsApps (ChangeID, AppID) VALUES (@ChangeID, @AppID)",
                    new MySqlParameter[]
                            {
                                new MySqlParameter("@ChangeID", callbackapp.Value.ChangeNumber),
                                new MySqlParameter("@AppID", callbackapp.Key)
                            });
                }

                foreach (var callbackpack in callback.PackageChanges)
                {
                    packageslist.Add(callbackpack.Key);
                    packageslistIRC.Add(callbackpack.Value.ID, callbackpack.Value.ChangeNumber);

                    DbWorker.ExecuteNonQuery("UPDATE Subs SET LastUpdated = CURRENT_TIMESTAMP WHERE SubID = @SubID",
                    new MySqlParameter[]
                            {
                                new MySqlParameter("@SubID", callbackpack.Key)
                            });
                    if (!callback.CurrentChangeNumber.Equals(callbackpack.Value.ChangeNumber))
                    {
                        DbWorker.ExecuteNonQuery("INSERT IGNORE INTO Changelists (ChangeID) VALUES (@ChangeID)",
                        new MySqlParameter[]
                                    {
                                        new MySqlParameter("@ChangeID", callbackpack.Value.ChangeNumber)
                                    });
                    }
                    DbWorker.ExecuteNonQuery("INSERT IGNORE INTO ChangelistsSubs (ChangeID, SubID) VALUES (@ChangeID, @SubID)",
                    new MySqlParameter[]
                            {
                                new MySqlParameter("@ChangeID", callbackpack.Value.ChangeNumber),
                                new MySqlParameter("@SubID", callbackpack.Key)
                            });
                }

                PreviousChange = callback.CurrentChangeNumber;

                steamApps.PICSGetProductInfo(appslist, packageslist, false, false);

                if (!callback.RequiresFullUpdate)
                {
                    System.Threading.ThreadPool.QueueUserWorkItem(delegate
                    {
                        ircSteam.OnPICSChanges(callback.CurrentChangeNumber, appslistIRC, packageslistIRC);
                    });
                }
            }
        }

        private void OnPICSProductInfo(SteamApps.PICSProductInfoCallback callback, JobID jobID)
        {
            var request = ircSteam.IRCRequests.Find(r => r.JobID == jobID);

            if (request != null)
            {
                ircSteam.IRCRequests.Remove(request);

                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    ircSteam.OnProductInfo(request, callback);
                });

                /*Task.Factory.StartNew(() =>
                {
                    ircSteam.OnProductInfo(request, callback);
                });*/

                return;
            }

            foreach (var app in callback.Apps)
            {
                Console.WriteLine("AppID: {0}", app.Key);

                var workaround = app;

                Task.Factory.StartNew(() =>
                {
                    AppPro.ProcessApp(workaround.Key, workaround.Value);
                });
            }

            foreach (var package in callback.Packages)
            {
                Console.WriteLine("SubID: {0}", package.Key);

                var workaround = package;

                Task.Factory.StartNew(() =>
                {
                    SubPro.ProcessSub(workaround.Key, workaround.Value);
                });
            }

            // Only handle when fullrun is disabled or if it specifically is running with mode "2" (full run inc. unknown apps)
            if (fullRunOption != 1)
            {
                foreach (uint app in callback.UnknownApps)
                {
                    uint workaround = app;

                    Task.Factory.StartNew(() =>
                    {
                        AppPro.ProcessUnknownApp(workaround);
                    });
                }

                foreach (uint package in callback.UnknownPackages)
                {
                    Console.WriteLine("Unknown SubID: {0} - We don't handle these yet", package);
                }
            }
        }
    }
}
