/*
 * Copyright (c) 2013, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 * 
 * Future non-SteamKit stuff should go in this file.
 */
using System;
using System.Threading;

namespace PICSUpdater
{
    class Program
    {
        static void Main(string[] args)
        {
            new Thread(new ThreadStart(Steam.Run)).Start();
            //Steam.GetPICSChanges();
        }
    }
}
