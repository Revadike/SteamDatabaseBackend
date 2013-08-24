/*
 * Copyright (c) 2013, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 * 
 * Future non-SteamKit stuff should go in this file.
 */
using System;
using System.Configuration;
using System.Threading;

namespace PICSUpdater
{
    class Program
    {
        static void Main(string[] args)
        {
            if (ConfigurationManager.AppSettings["steam-username"] == null || ConfigurationManager.AppSettings["steam-password"] == null)
            {
                Console.WriteLine("Is config missing? It should be in " + ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None).FilePath);
                return;
            }

            //new Thread(new ThreadStart(Steam.Run)).Start();
            Steam.Run();
        }
    }
}
