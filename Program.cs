/*
 * Copyright (c) 2013, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 * 
 * Future non-SteamKit stuff should go in this file.
 */
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using SteamKit2;

namespace SteamDatabaseBackend
{
    internal static class Program
    {
        private static List<GCIdler> GCIdlers = new List<GCIdler>();

        public static void Main()
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

            AppDomain.CurrentDomain.UnhandledException += OnSillyCrashHandler;

            Console.CancelKeyPress += delegate
            {
                Cleanup();
            };

            Thread thread = new Thread(new ThreadStart(Steam.Instance.Init));
            thread.Name = "Steam";
            thread.Start();

            if (Settings.Current.FullRun > 0)
            {
                Settings.Current.IRC.Enabled = false;

                return;
            }

            foreach (var idler in Settings.Current.GameCoordinatorIdlers)
            {
                if (string.IsNullOrWhiteSpace(idler.Username) || string.IsNullOrWhiteSpace(idler.Password) || idler.AppID <= 0)
                {
                    Log.WriteWarn("Settings", "Invalid GC coordinator settings");
                    continue;
                }

                Log.WriteInfo("Main", "Starting GC idler for app {0}", idler.AppID);

                var instance = new GCIdler(idler.AppID, idler.Username, idler.Password);

                thread = new Thread(new ThreadStart(instance.Run));
                thread.Name = "Steam";
                thread.Start();

                GCIdlers.Add(instance);
            }

            if (Settings.CanConnectToIRC())
            {
                SteamProxy.Instance.ReloadImportant();

                IRC.Instance.Init();
            }
        }

        private static void OnSillyCrashHandler(object sender, UnhandledExceptionEventArgs args)
        {
            Exception e = (Exception)args.ExceptionObject;

            Log.WriteError("Unhandled Exception", "{0} (is terminating: {1})", e.Message, args.IsTerminating);

            if (args.IsTerminating)
            {
                IRC.SendMain("Hey, xPaw and Alram, I'm crashing over here!!");

                Cleanup();
            }
        }

        private static void Cleanup()
        {
            Log.WriteInfo("Main", "Exiting...");

            foreach (var idler in GCIdlers)
            {
                try
                {
                    idler.IsRunning = false;
                    idler.Client.Disconnect();
                }
                catch { }
            }

            Steam.Instance.IsRunning = false;

            try { Steam.Instance.Timer.Stop();                       } catch { }
            try { DepotProcessor.ThreadPool.Shutdown(true, 1000);    } catch { }
            try { Steam.Instance.SecondaryPool.Shutdown(true, 1000); } catch { }
            try { Steam.Instance.ProcessorPool.Shutdown(true, 1000); } catch { }
            try { Steam.Instance.Client.Disconnect();                } catch { }

            if (Settings.Current.IRC.Enabled)
            {
                IRC.Instance.Kill();
            }

            DbWorker.ExecuteNonQuery("TRUNCATE TABLE `GC`");
        }
    }
}
