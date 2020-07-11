/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using SteamKit2;

namespace SteamDatabaseBackend
{
    internal static class Utils
    {
        private static readonly Random RandomGenerator = new Random();
        public static SHA1 Sha1Instance { get; } = SHA1.Create();
        public static HttpClient HttpClient { get; }

        static Utils()
        {
            HttpClient = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.GZip,
            });
            HttpClient.DefaultRequestHeaders.Add("User-Agent", SteamDB.UserAgent);
            HttpClient.Timeout = TimeSpan.FromSeconds(10);
        }

        public static IEnumerable<IEnumerable<T>> Split<T>(this IEnumerable<T> fullBatch, int chunkSize)
        {
            while (fullBatch.Any())
            {
                yield return fullBatch.Take(chunkSize);
                fullBatch = fullBatch.Skip(chunkSize);
            }
        }

        public static int NextRandom(int maxValue)
        {
            lock (RandomGenerator)
            {
                return RandomGenerator.Next(maxValue);
            }
        }

        public static int ExponentionalBackoff(int i)
        {
            return ((1 << i) * 1000) + NextRandom(1001);
        }

        public static byte[] AdlerHash(byte[] input)
        {
            uint a = 0, b = 0;
            foreach (var t in input)
            {
                a = (a + t) % 65521;
                b = (b + a) % 65521;
            }
            return BitConverter.GetBytes(a | (b << 16));
        }

        public static byte[] StringToByteArray(string str)
        {
            var HexAsBytes = new byte[str.Length / 2];

            for (var index = 0; index < HexAsBytes.Length; index++)
            {
                var byteValue = str.Substring(index * 2, 2);
                HexAsBytes[index] = byte.Parse(byteValue, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }

            return HexAsBytes;
        }

        public static string ByteArrayToString(byte[] ba)
        {
            return BitConverter.ToString(ba).Replace("-", "");
        }

        public static bool ByteArrayEquals(byte[] a, byte[] b)
        {
            // returns when a and b are both null
            if (a == b)
            {
                return true;
            }

            // if either is null can't be equal
            if (a == null || b == null)
            {
                return false;
            }

            return a.SequenceEqual(b);
        }

        public static string RemoveControlCharacters(string input)
        {
            return new string(input.Where(c => !char.IsControl(c)).ToArray());
        }

        public static string LimitStringLength(string input)
        {
            if (input.Length <= 100)
            {
                return input;
            }

            return input.Substring(0, 100) + "…";
        }

        public static string JsonifyKeyValue(KeyValue keys)
        {
            using var sw = new StringWriter(new StringBuilder());
            using JsonWriter w = new JsonTextWriter(sw);
            JsonifyKeyValue(w, keys.Children);

            return sw.ToString();
        }

        private static void JsonifyKeyValue(JsonWriter w, List<KeyValue> keys)
        {
            w.WriteStartObject();

            foreach (var keyval in keys)
            {
                if (keyval.Children.Count > 0)
                {
                    w.WritePropertyName(keyval.Name);
                    JsonifyKeyValue(w, keyval.Children);
                }
                else if (keyval.Value != null) // TODO: Should we be writing null keys anyway?
                {
                    w.WritePropertyName(keyval.Name);
                    w.WriteValue(keyval.Value);
                }
            }

            w.WriteEndObject();
        }
    }
}
