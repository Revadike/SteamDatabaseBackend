/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
namespace SteamDatabaseBackend
{
    class PICSHistory
    {
        public uint ID { get; set; } // AppID or SubID 
        public uint ChangeID { get; set; }
        public uint Key { get; set; }
        public string Action { get; set; }

        private string _oldValue = "";
        private string _newValue = "";

        public string OldValue
        {
            get
            {
                return _oldValue;
            }
            set
            {
                _oldValue = value;
            }
        }

        public string NewValue
        {
            get
            {
                return _newValue;
            }
            set
            {
                _newValue = value;
            }
        }
    }
}
