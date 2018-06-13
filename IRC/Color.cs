/*
 * Copyright (c) 2013-2018, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System.Text.RegularExpressions;

namespace SteamDatabaseBackend
{
    static class Colors
    {
        private const char IrcColor = '\x3';

        // To keep our colors somewhat consistent, only used colors are uncommented
        public const char NORMAL = '\x0F';
        //public static readonly string WHITE = IrcColor + "00"; // white is evil
        //public static readonly string BLACK = IrcColor + "01"; // black is evil, too
        public static readonly string DARKBLUE = IrcColor + "02";
        //public static readonly string DARKGREEN = IrcColor + "03";
        public static readonly string RED = IrcColor + "04";
        //public static readonly string BROWN = IrcColor + "05";
        //public static readonly string PURPLE = IrcColor + "06";
        public static readonly string OLIVE = IrcColor + "07";
        //public static readonly string YELLOW = IrcColor + "08";
        public static readonly string GREEN = IrcColor + "09";
        //public static readonly string TEAL = IrcColor + "10";
        //public static readonly string CYAN = IrcColor + "11";
        public static readonly string BLUE = IrcColor + "12";
        //public static readonly string MAGENTA = IrcColor + "13";
        public static readonly string DARKGRAY = IrcColor + "14";
        public static readonly string LIGHTGRAY = IrcColor + "15";

        public static string StripColors(string input)
        {
            return Regex.Replace(input, @"\x0F|\x03(\d\d)?", string.Empty);
        }
    }
}
