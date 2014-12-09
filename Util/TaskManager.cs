/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Threading.Tasks;
using Bugsnag.Library;

namespace SteamDatabaseBackend
{
    static class TaskManager
    {
        public static Task Run(Action action)
        {
            return Task.Run(() =>
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    Log.WriteError("TaskManager", "Exception: {1}\n{2}", e.Message, e.StackTrace);

                    var bugsnag = new BugSnag();
                    bugsnag.Notify(e);
                }
            });
        }
    }
}
