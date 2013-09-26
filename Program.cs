/*
 * Copyright (c) 2013, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 * 
 * Future non-SteamKit stuff should go in this file.
 */
using System;
using System.Reflection;
using System.Threading;
using Meebey.SmartIrc4net;
using SteamKit2;

namespace SteamDatabaseBackend
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            string date = new DateTime(2000, 01, 01).AddDays(version.Build).AddSeconds(version.Revision * 2).ToUniversalTime().ToString();

            Log.WriteInfo("Main", "Built on {0} UTC", date);

            try
            {
                Settings.Load();
            }
            catch (Exception e)
            {
                Log.WriteError("Settings", "{0}", e.Message);

                return;
            }

            if (Settings.Current.SteamKitDebug)
            {
                DebugLog.AddListener(new Log.SteamKitLogger());
                DebugLog.Enabled = true;
            }

            Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e)
            {
                Log.WriteInfo("Main", "Exiting...");

                Steam.Instance.IsRunning = false;
                SteamDota.Instance.IsRunning = false;

                try { Steam.Instance.Timer.Stop();                       } catch (Exception) { }
                try { Steam.Instance.SecondaryPool.Shutdown(true, 1000); } catch (Exception) { }
                try { Steam.Instance.ProcessorPool.Shutdown(true, 1000); } catch (Exception) { }
                try { DepotProcessor.ThreadPool.Shutdown(true, 1000);    } catch (Exception) { }
                try { Steam.Instance.Client.Disconnect();                } catch (Exception) { }

                if (SteamDota.Instance.Client != null)
                {
                    try { SteamDota.Instance.Client.Disconnect(); } catch (Exception) { }
                }

                IRC.Instance.Kill();
            };

            RunSteam();

            if (Settings.Current.FullRun > 0)
            {
                return;
            }

            if (Settings.CanUseDota())
            {
                RunDoto();
            }

            if (Settings.CanConnectToIRC())
            {
                SteamProxy.Instance.Init();

                IRC.Instance.Init();
            }
        }

        private static void RunSteam()
        {
            Thread thread = new Thread(new ThreadStart(Steam.Instance.Init));
            thread.Name = "Steam";
            thread.Start();
        }

        private static void RunDoto()
        {
            Thread thread = new Thread(new ThreadStart(SteamDota.Instance.Init));
            thread.Name = "Steam Dota";
            thread.Start();
        }
    }
}
