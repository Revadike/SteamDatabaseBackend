/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Diagnostics;

namespace SteamDatabaseBackend
{
    internal static class ErrorReporter
    {
        public static void Notify(string component, Exception e)
        {
            Log.WriteError(component, "Exception: {0}", e);

            var stacktrace = new StackTrace(e, true);
            var frame = stacktrace.GetFrame(stacktrace.FrameCount - 1);

            if (frame == null)
            {
                IRC.Instance.SendOps($"⚠️ {Colors.OLIVE}[{component} Exception]{Colors.NORMAL} {e.Message}");

                return;
            }

            IRC.Instance.SendOps($"⚠️ {Colors.OLIVE}[{component} Exception @{frame.GetMethod()}]{Colors.NORMAL} {e.Message} {Colors.DARKGRAY}({frame.GetFileName()}#L{frame.GetFileLineNumber()})");
        }
    }
}
