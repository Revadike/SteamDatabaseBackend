/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 * 
 * Future non-SteamKit stuff should go in this file.
 */
using System;
using System.Reflection;
using SteamKit2;

namespace SteamDatabaseBackend
{
    internal static class Program
    {
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

            Application.Instance.Init();
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
            Log.WriteInfo("Main", "Exiting... ({0} processor, {1} secondary)", Application.Instance.ProcessorPool.InUseThreads, Application.Instance.SecondaryPool.InUseThreads);

            foreach (var idler in Application.Instance.GCIdlers)
            {
                try
                {
                    idler.IsRunning = false;
                    idler.Client.Disconnect();
                }
                catch { }
            }

            Steam.Instance.IsRunning = false;

            try { Application.Instance.Timer.Stop();                       } catch { }
            try { Application.Instance.SecondaryPool.Shutdown(true, 1000); } catch { }
            try { Application.Instance.ProcessorPool.Shutdown(true, 1000); } catch { }
            try { Steam.Instance.Client.Disconnect();                } catch { }

            if (Settings.Current.IRC.Enabled)
            {
                IRC.Instance.Kill();
            }

            DbWorker.ExecuteNonQuery("DELETE FROM `GC`");
        }
    }
}
