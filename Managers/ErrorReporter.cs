/*
 * Copyright (c) 2013-2018, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Diagnostics;

namespace SteamDatabaseBackend
{
    static class ErrorReporter
    {
        public static void Notify(string component, Exception e)
        {
            Log.WriteError(component, "Exception: {0}", e);

            var stacktrace = new StackTrace(e, true);
            var frame = stacktrace.GetFrame(stacktrace.FrameCount - 1);

            IRC.Instance.SendOps("⚠️ {0}[{1} Exception @{2}]{3} {4} {5}({6}#L{7})",
                Colors.OLIVE, component, frame.GetMethod(), Colors.NORMAL,
                e.Message, Colors.DARKGRAY, frame.GetFileName(), frame.GetFileLineNumber()
            );
        }
    }
}
