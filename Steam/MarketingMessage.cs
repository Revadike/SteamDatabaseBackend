/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using SteamKit2;
using MySql.Data.MySqlClient;

namespace SteamDatabaseBackend
{
    public class MarketingMessage
    {
        public MarketingMessage()
        {
            Steam.Instance.CallbackManager.Register(new Callback<SteamUser.MarketingMessageCallback>(OnMarketingMessage));
        }

        private static void OnMarketingMessage(SteamUser.MarketingMessageCallback callback)
        {
            foreach (var message in callback.Messages)
            {
                // TODO: Move this query outside this loop
                using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `ID` FROM `MarketingMessages` WHERE `ID` = @ID", new MySqlParameter("ID", message.ID)))
                {
                    if (Reader.Read())
                    {
                        continue;
                    }
                }

                if (message.Flags == EMarketingMessageFlags.None)
                {
                    IRC.SendMain("New marketing message:{0} {1}", Colors.DARK_BLUE, message.URL);
                }
                else
                {
                    IRC.SendMain("New marketing message:{0} {1} {2}({3})", Colors.DARK_BLUE, message.URL, Colors.DARK_GRAY, message.Flags.ToString().Replace("Platform", string.Empty));
                }

                DbWorker.ExecuteNonQuery("INSERT INTO `MarketingMessages` (`ID`, `Flags`) VALUES (@ID, @Flags)",
                    new MySqlParameter("@ID", message.ID),
                    new MySqlParameter("@Flags", message.Flags)
                );
            }
        }
    }
}
    