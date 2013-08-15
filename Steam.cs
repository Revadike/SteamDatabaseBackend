/*
 * Copyright (c) 2013, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Configuration;
using System.Threading;
using SteamKit2;
using MySql.Data.MySqlClient;

namespace PICSUpdater
{
    class Steam
    {
        static SteamClient steamClient = new SteamClient();
        static SteamUser steamUser = steamClient.GetHandler<SteamUser>();
        static SteamApps steamApps = steamClient.GetHandler<SteamApps>();
        static SteamFriends steamFriends = steamClient.GetHandler<SteamFriends>();

        static CallbackManager manager;

        static uint PreviousChange = 0;
        static string PrevChangeFile = @"lastchangenumber";
        static Boolean LoggedIn = false;
        static AppProcessor AppPro = new AppProcessor();
        static SubProcessor SubPro = new SubProcessor();

        public static void GetPICSChanges()
        {
            steamApps.PICSGetChangesSince(PreviousChange, true, true);
        }

        public static void Run()
        {
            DebugLog.AddListener( ( category, msg ) => Console.WriteLine( "[SteamKit] {0}: {1}", category, msg ) );

            manager = new CallbackManager(steamClient);

            new Callback<SteamClient.ConnectedCallback>(OnConnected, manager);
            new Callback<SteamClient.DisconnectedCallback>(OnDisconnected, manager);

            new Callback<SteamUser.AccountInfoCallback>(OnAccountInfo, manager);
            new Callback<SteamUser.LoggedOnCallback>(OnLoggedOn, manager);

            new Callback<SteamFriends.FriendMsgCallback>(OnFriendMsg, manager);

            new JobCallback<SteamApps.PICSChangesCallback>(OnPICSChanges, manager);
            new JobCallback<SteamApps.PICSProductInfoCallback>(OnPICSProductInfo, manager);


            if (File.Exists(PrevChangeFile))
            {
                PreviousChange = uint.Parse(File.ReadAllText(PrevChangeFile).ToString());
            }
            else
            {
                File.WriteAllText(PrevChangeFile, "0");
            }

            steamClient.Connect();

            bool isRunning = true;

            while (isRunning == true)
            {
                manager.RunWaitCallbacks(TimeSpan.FromSeconds(1));
            }
        }
        static void OnConnected(SteamClient.ConnectedCallback callback)
        {
            if (callback.Result != EResult.OK)
            {
                throw new Exception("Could not connect: " + callback.Result);
            }

            Console.WriteLine("Connected! Logging in...");

            steamUser.LogOn(new SteamUser.LogOnDetails
            {
                Username = ConfigurationManager.AppSettings["steam-username"],
                Password = ConfigurationManager.AppSettings["steam-password"]
            });
        }

        static void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            LoggedIn = false;
            Console.WriteLine("Disconnected from Steam. Retrying in 15 seconds..");
            Thread.Sleep(TimeSpan.FromSeconds(15));
            steamClient.Connect();
        }

        static void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            if (callback.Result != EResult.OK)
            {
                Console.WriteLine("Login failed. Retrying in 2 seconds..");
                Thread.Sleep(TimeSpan.FromSeconds(2));
            }
            else
            {
                Console.WriteLine("Logged on.");
                LoggedIn = true;
                GetPICSChanges();
            }

            if(ConfigurationManager.AppSettings["fullrun"].Equals("1")){
         
            uint buildlistcount = 0;
            List<uint> appIdList = new List<uint>();
            List<uint> packagesList = new List<uint>();
            while (buildlistcount < 250000)
            {
                appIdList.Add(buildlistcount);
                //packagesList.Add(buildlistcount);
                buildlistcount++;
            }
            steamApps.PICSGetProductInfo(appIdList, packagesList, false, false);

            }
        }

        static void OnAccountInfo(SteamUser.AccountInfoCallback callback)
        {
            steamFriends.SetPersonaState(EPersonaState.Busy);
        }

        static void OnFriendMsg(SteamFriends.FriendMsgCallback callback)
        {
            if (callback.Message.ToString() == "retry")
            {
                steamFriends.SendChatMessage(callback.Sender, EChatEntryType.ChatMsg, "Sent previous changelist again!");
                GetPICSChanges();
            }
            else if (callback.Message.ToString() == "lastchangelist")
            {
                steamFriends.SendChatMessage(callback.Sender, EChatEntryType.ChatMsg, PreviousChange.ToString());
            }
            else if (callback.Message.ToString().Contains("forceapp"))
            {
                string[] exploded = callback.Message.ToString().Split(' ');
                uint appid;
                if (exploded.Count() == 2)
                {
                    if (uint.TryParse(exploded[1], out appid))
                    {
                        steamApps.PICSGetProductInfo(appid, null, false, false);
                    }
                }

            }
            else if (callback.Message.ToString().Contains("forcesub"))
            {
                string[] exploded = callback.Message.ToString().Split(' ');
                uint subid;
                if (exploded.Count() == 2)
                {
                    if (uint.TryParse(exploded[1], out subid))
                    {
                        steamApps.PICSGetProductInfo(null, subid, false, false);
                    }
                }
            }
        }

        static void OnPICSChanges(SteamApps.PICSChangesCallback callback, JobID job)
        {
            if (PreviousChange.Equals(0))
            {
                Console.WriteLine("PreviousChange was 0. Rolling back by one changelist.");
                PreviousChange = callback.CurrentChangeNumber - 1;
                GetPICSChanges();
                return;
            }

            if (PreviousChange != callback.CurrentChangeNumber)
            {
                Console.WriteLine(PreviousChange + " and " + callback.CurrentChangeNumber + " differ!");
                List<uint> appslist = new List<uint>();
                List<uint> packageslist = new List<uint>();
                DbWorker.ExecuteNonQuery("INSERT INTO Changelists (ChangeID) VALUES (@ChangeID) ON DUPLICATE KEY UPDATE date = NOW()",
                new MySqlParameter[]
                                    {
                                        new MySqlParameter("@ChangeID", callback.CurrentChangeNumber)
                                    });

                foreach (var callbackapp in callback.AppChanges)
                {
                    appslist.Add(callbackapp.Key);
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
                File.WriteAllText(PrevChangeFile, callback.CurrentChangeNumber.ToString());
                steamApps.PICSGetProductInfo(appslist, packageslist, false, false);
            }

            //TODO, get rid of this and do it every second anyhow
            GetPICSChanges();
        }

        static void OnPICSProductInfo(SteamApps.PICSProductInfoCallback callback, JobID job)
        {
            foreach (var callbackapp in callback.Apps)
            {
                Console.WriteLine("AppID: " + callbackapp.Key);
                new System.Threading.Thread(AppPro.ProcessApp).Start(callbackapp);
                System.Threading.Thread.Sleep(20);
            }
            foreach (var callbackpack in callback.Packages)
            {
                Console.WriteLine("SubID: " + callbackpack.Key);
                new System.Threading.Thread(SubPro.ProcessSub).Start(callbackpack);
                System.Threading.Thread.Sleep(20);
            }
            foreach (var unknownapp in callback.UnknownApps)
            {
                new System.Threading.Thread(AppPro.ProcessUnknownApp).Start(unknownapp);
                System.Threading.Thread.Sleep(20);
            }
            foreach (var unknownpack in callback.UnknownPackages)
            {
                Console.WriteLine("Got an unknown Sub. We don't handle these yet. (" + unknownpack + ")");
            }
        }

    }
}
