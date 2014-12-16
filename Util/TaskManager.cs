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
            var t = new Task(action);
                
            t.ContinueWith(task =>
            {
                var bugsnag = new BugSnag();

                task.Exception.Flatten().Handle(e =>
                {
                    Log.WriteError("Task Manager", "Exception: {0}\n{1}", e.Message, e.StackTrace);

                    bugsnag.Notify(e);

                    return false;
                });
            }, TaskContinuationOptions.OnlyOnFaulted);

            t.Start();

            return t;
        }
    }
}
