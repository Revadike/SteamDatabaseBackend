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
                if (!task.IsFaulted)
                {
                    Log.WriteError("Task Manager", "Task got cancelled? ({0})", action.ToString());

                    return;
                }

                task.Exception.Handle(e =>
                {
                    Log.WriteError("Task Manager", "Exception: {0}\n{1}", e.Message, e.StackTrace);

                    var bugsnag = new BugSnag();
                    bugsnag.Notify(e);

                    return false;
                });
            }, TaskContinuationOptions.NotOnRanToCompletion);

            t.Start();

            return t;
        }
    }
}
