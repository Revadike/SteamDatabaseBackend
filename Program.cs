/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
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
        public static readonly List<GCIdler> GCIdlers = new List<GCIdler>();

        public static void Main()
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            string date = new DateTime(2000, 01, 01).AddDays(version.Build).AddSeconds(version.Revision * 2).ToUniversalTime().ToString();

            Log.WriteInfo("Main", "Steam Database backend application. Built on {0} UTC", date);
            Log.WriteInfo("Main", "Copyright (c) 2013-2015, SteamDB. See LICENSE file for more information.");

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

            var thread = new Thread(new ThreadStart(Steam.Instance.Init));
            thread.Name = "Steam";
            thread.Start();

            // We don't need GC idlers in full run
            if (Settings.IsFullRun)
            {
                return;
            }

            foreach (var appID in Settings.Current.GameCoordinatorIdlers)
            {
                var instance = new GCIdler(appID);

                thread = new Thread(new ThreadStart(instance.Run));
                thread.Name = string.Format("GC Idler {0}", appID);
                thread.Start();

                GCIdlers.Add(instance);
            }
        }

        private static void OnSillyCrashHandler(object sender, UnhandledExceptionEventArgs args)
        {
            var e = (Exception)args.ExceptionObject;

            Log.WriteError("Unhandled Exception", "{0} (is terminating: {1})\n{2}", e.Message, args.IsTerminating, e.StackTrace);

            if (args.IsTerminating)
            {
                IRC.Instance.SendMain("Hey, xPaw and Alram, I'm crashing over here!!");

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
            try { Steam.Instance.SecondaryPool.Shutdown(true, 1000); } catch { }
            try { Steam.Instance.ProcessorPool.Shutdown(true, 1000); } catch { }
            try { Steam.Instance.Client.Disconnect();                } catch { }

            if (Settings.Current.IRC.Enabled)
            {
                IRC.Instance.Kill();
            }

            DbWorker.ExecuteNonQuery("DELETE FROM `GC`");
        }
    }
}
