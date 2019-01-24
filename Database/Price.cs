/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
namespace SteamDatabaseBackend
{
    class Price
    {
        public string Country { get; set; }
        public uint PriceFinal { get; set; }
        public uint PriceDiscount { get; set; }

        public string Format()
        {
            double cents = PriceFinal / 100.0;
            var discount = PriceDiscount > 0 ? string.Format(" at -{0}%", PriceDiscount) : string.Empty;

            switch (Country)
            {
                case "uk": return string.Format("£{0:0.00}{1}", cents, discount);
                case "us": return string.Format("${0:0.00}{1}", cents, discount);
                case "eu": return string.Format("{0:0.00}€{1}", cents, discount).Replace('.', ',').Replace(",00", ",--");
            }

            return string.Format("{1}: {0}", cents, Country);
        }
    }
}
