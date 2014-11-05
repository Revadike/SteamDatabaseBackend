/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 * 
 * Future non-SteamKit stuff should go in this file.
 */
using System;
using System.Reflection;
using System.Threading;
using SteamKit2;

namespace SteamDatabaseBackend
{
    static class Bootstrapper
    {
        private static bool CleaningUp;

        public static void Main()
        {
            Console.Title = "Steam Database";

            var version = Assembly.GetExecutingAssembly().GetName().Version;
            var date = new DateTime(2000, 01, 01).AddDays(version.Build).AddSeconds(version.Revision * 2).ToUniversalTime().ToString();

            Log.WriteInfo("Bootstrapper", "Steam Database backend application. Built on {0} UTC", date);
            Log.WriteInfo("Bootstrapper", "Copyright (c) 2013-2015, SteamDB. See LICENSE file for more information.");

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

            Console.CancelKeyPress += OnCancelKey;

            Application.Init();
        }

        private static void OnCancelKey(object sender, ConsoleCancelEventArgs e)
        {
            if (CleaningUp)
            {
                Log.WriteInfo("Bootstrapper", "Forcing exit");

                Environment.Exit(0);

                return;
            }

            e.Cancel = true;

            CleaningUp = true;

            Cleanup();
        }

        private static void OnSillyCrashHandler(object sender, UnhandledExceptionEventArgs args)
        {
            var e = (Exception)args.ExceptionObject;

            Log.WriteError("Unhandled Exception", "{0}\n{1}", e.Message, e.StackTrace);

            if (args.IsTerminating)
            {
                AppDomain.CurrentDomain.UnhandledException -= OnSillyCrashHandler;

                IRC.Instance.SendOps("I am literally about to crash, send help.");

                Cleanup();
            }
        }

        private static void Cleanup()
        {
            Log.WriteInfo("Bootstrapper", "Exiting...");

            Application.ChangelistTimer.Stop();

            Steam.Instance.IsRunning = false;

            foreach (var idler in Application.GCIdlers)
            {
                Log.WriteInfo("Application", "Disconnecting idler {0}", idler.AppID);

                try
                {
                    idler.IsRunning = false;
                    idler.Client.Disconnect();
                }
                catch { }
            }

            Log.WriteInfo("Bootstrapper", "Waiting for processor pool to idle ({0} threads in use)", Application.ProcessorPool.InUseThreads);

            Application.ProcessorPool.WaitForIdle();

            Log.WriteInfo("Bootstrapper", "Waiting for secondary pool to idle ({0} threads in use)", Application.SecondaryPool.InUseThreads);

            Application.SecondaryPool.WaitForIdle();

            Log.WriteInfo("Bootstrapper", "Shutting down pools");

            try { Application.SecondaryPool.Shutdown(true, 1000); } catch { }
            try { Application.ProcessorPool.Shutdown(true, 1000); } catch { }

            Log.WriteInfo("Bootstrapper", "Disconnecting from Steam");

            try { Steam.Instance.Client.Disconnect();             } catch { }

            if (Settings.Current.IRC.Enabled)
            {
                Log.WriteInfo("Bootstrapper", "Closing IRC connection");

                IRC.Instance.Close(CleaningUp);
            }

            foreach (var thread in Application.Threads)
            {
                if (thread.ThreadState == ThreadState.Running)
                {
                    Log.WriteInfo("Bootstrapper", "Joining thread {0}", thread.Name);

                    thread.Join(TimeSpan.FromSeconds(30));
                }
            }

            DbWorker.ExecuteNonQuery("DELETE FROM `GC`");
        }
    }
}
