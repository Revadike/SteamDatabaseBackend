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
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SteamKit2;

namespace SteamDatabaseBackend
{
    internal static class Utils
    {
        private static readonly Random RandomGenerator = new Random();
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

        public static async Task SendWebhook(object payload)
        {
            if (Settings.Current.WebhookURL == null)
            {
                return;
            }

            var json = JsonConvert.SerializeObject(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            try
            {
                var result = await HttpClient.PostAsync(Settings.Current.WebhookURL, content);
                var output = await result.Content.ReadAsStringAsync();

                Log.WriteDebug("Webhook", $"Result: {output}");
            }
            catch (Exception e)
            {
                ErrorReporter.Notify("Webhook", e);
            }
        }

        private static void JsonifyKeyValue(JsonWriter w, List<KeyValue> keys)
        {
            w.WriteStartObject();

            foreach (var keyval in keys)
            {
                if (keyval.Value != null)
                {
                    w.WritePropertyName(keyval.Name);
                    w.WriteValue(keyval.Value);
                }
                else
                {
                    w.WritePropertyName(keyval.Name);
                    JsonifyKeyValue(w, keyval.Children);
                }
            }

            w.WriteEndObject();
        }
    }
}
