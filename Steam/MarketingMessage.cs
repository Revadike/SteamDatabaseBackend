/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using MySql.Data.MySqlClient;
using SteamKit2;

namespace SteamDatabaseBackend
{
    class MarketingMessage : SteamHandler
    {
        public MarketingMessage(CallbackManager manager)
            : base(manager)
        {
            manager.Register(new Callback<SteamUser.MarketingMessageCallback>(OnMarketingMessage));
        }

        private static void OnMarketingMessage(SteamUser.MarketingMessageCallback callback)
        {
            foreach (var message in callback.Messages)
            {
                // TODO: Move this query outside this loop
                using (var reader = DbWorker.ExecuteReader("SELECT `ID` FROM `MarketingMessages` WHERE `ID` = @ID", new MySqlParameter("ID", message.ID)))
                {
                    if (reader.Read())
                    {
                        continue;
                    }
                }

                if (message.Flags == EMarketingMessageFlags.None)
                {
                    IRC.Instance.SendMain("New marketing message:{0} {1}", Colors.DARKBLUE, message.URL);
                }
                else
                {
                    IRC.Instance.SendMain("New marketing message:{0} {1} {2}({3})", Colors.DARKBLUE, message.URL, Colors.DARKGRAY, message.Flags.ToString().Replace("Platform", string.Empty));
                }

                DbWorker.ExecuteNonQuery(
                    "INSERT INTO `MarketingMessages` (`ID`, `Flags`) VALUES (@ID, @Flags)",
                    new MySqlParameter("@ID", message.ID),
                    new MySqlParameter("@Flags", message.Flags)
                );
            }
        }
    }
}
