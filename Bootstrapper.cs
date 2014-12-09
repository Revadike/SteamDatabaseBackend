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
using System.Threading.Tasks;
using Bugsnag.Library;
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
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

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

        private static void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs args)
        {
            var e = args.Exception;

            Log.WriteError("Unhandled Task Exception", "{0}\n{1}", e.Message, e.StackTrace);

            var bugsnag = new BugSnag();
            bugsnag.Notify(e);
        }

        private static void OnSillyCrashHandler(object sender, UnhandledExceptionEventArgs args)
        {
            var e = args.ExceptionObject as Exception;

            Log.WriteError("Unhandled Exception", "{0}\n{1}", e.Message, e.StackTrace);

            if (args.IsTerminating)
            {
                AppDomain.CurrentDomain.UnhandledException -= OnSillyCrashHandler;

                IRC.Instance.SendOps("I am literally about to crash, send help.");

                Cleanup();
            }

            var bugsnag = new BugSnag();
            bugsnag.Notify(e, new
            {
                SteamIsConnected = Steam.Instance.Client.IsConnected,
                PreviousChangeNumber = Steam.Instance.PICSChanges.PreviousChangeNumber
            });
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
