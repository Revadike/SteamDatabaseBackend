/*
 * Copyright (c) 2013-2018, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using SteamKit2;

namespace SteamDatabaseBackend
{
    static class Utils
    {
        private static readonly Random RandomGenerator = new Random();

        // Adapted from http://stackoverflow.com/a/13503860/139147
        // Mono doesn't really like method extensions
        public static IEnumerable<TResult> FullOuterJoin<TLeft, TRight, TKey, TResult>(
            /*            this*/ IEnumerable<TLeft> left,
            IEnumerable<TRight> right,
            Func<TLeft, TKey> leftKeySelector,
            Func<TRight, TKey> rightKeySelector,
            Func<TLeft, TRight, TKey, TResult> resultSelector,
            TLeft defaultLeft = default(TLeft),
            TRight defaultRight = default(TRight))
        {
            var leftLookup = left.ToLookup(leftKeySelector);
            var rightLookup = right.ToLookup(rightKeySelector);

            var leftKeys = leftLookup.Select(l => l.Key);
            var rightKeys = rightLookup.Select(r => r.Key);

            var keySet = new HashSet<TKey>(leftKeys.Union(rightKeys));

            return from key in keySet
                from leftValue in leftLookup[key].DefaultIfEmpty(defaultLeft)
                from rightValue in rightLookup[key].DefaultIfEmpty(defaultRight)
                select resultSelector(leftValue, rightValue, key);
        }

        // https://codereview.stackexchange.com/a/90531/151882
        public static IEnumerable<IEnumerable<T>> Split<T>(this IEnumerable<T> fullBatch, int chunkSize)
        {
            var cellCounter = 0;
            var chunk = new List<T>(chunkSize);

            foreach (var element in fullBatch)
            {
                if (cellCounter++ == chunkSize)
                {
                    yield return chunk;
                    chunk = new List<T>(chunkSize);
                    cellCounter = 1;
                }

                chunk.Add(element);
            }

            yield return chunk;
        }

        // https://stackoverflow.com/a/33551927/2200891
        public static IEnumerable<T> DequeueChunk<T>(this Queue<T> queue, int chunkSize)
        {
            for (var i = 0; i < chunkSize && queue.Count > 0; i++)
            {
                yield return queue.Dequeue();
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
            return (1 << i) * 1000 + NextRandom(1001);
        }

        public static SteamApps.PICSRequest NewPICSRequest(uint id)
        {
            return new SteamApps.PICSRequest(id, PICSTokens.GetToken(id), false);
        }

        public static SteamApps.PICSRequest NewPICSRequest(uint id, ulong accessToken)
        {
            if (accessToken > 0)
            {
                PICSTokens.HandleToken(id, accessToken);
            }

            return new SteamApps.PICSRequest(id, accessToken, false);
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

            for (int index = 0; index < HexAsBytes.Length; index++)
            {
                string byteValue = str.Substring(index * 2, 2);
                HexAsBytes[index] = byte.Parse(byteValue, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }

            return HexAsBytes; 
        }

        public static string ByteArrayToString(byte[] ba)
        {
            return BitConverter.ToString(ba).Replace("-", "");
        }

        public static bool IsEqualSHA1(byte[] a, byte[] b)
        {
            const int SHA1_LENGTH = 20;

            var aEmpty = a == null || a.Length < SHA1_LENGTH;
            var bEmpty = b == null || b.Length < SHA1_LENGTH;

            if (aEmpty || bEmpty)
            {
                return aEmpty == bEmpty;
            }

            for (int i = 0; i < SHA1_LENGTH; ++i)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }

            return true;
        }

        public static bool ConvertUserInputToSQLSearch(ref string output)
        {
            if (output.Length < 2 || !output.Distinct().Skip(1).Any()) // TODO: Probably would be better to only search for % and _ repetitions
            {
                return false;
            }

            if (output[0] == '^')
            {
                output = output.Substring(1);
            }
            else
            {
                output = "%" + output;
            }

            if (output[output.Length - 1] == '$')
            {
                output = output.Substring(0, output.Length - 1);
            }
            else
            {
                output += "%";
            }

            if (output.Length == 0)
            {
                return false;
            }

            var distinct = output.Distinct().ToList();

            if (distinct.Count == 1 && distinct.First() == '%')
            {
                return false;
            }

            return true;
        }

        public static string RemoveControlCharacters(string input)
        {
            return new string(input.Where(c => !char.IsControl(c)).ToArray());
        }

        public static string JsonifyKeyValue(KeyValue keys)
        {
            string value;

            using (var sw = new StringWriter(new StringBuilder()))
            {
                using (JsonWriter w = new JsonTextWriter(sw))
                {
                    JsonifyKeyValue(w, keys.Children);
                }

                value = sw.ToString();
            }

            return value;
        }

        private static void JsonifyKeyValue(JsonWriter w, List<KeyValue> keys)
        {
            w.WriteStartObject();

            foreach (KeyValue keyval in keys)
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

    class EmptyGrouping<TKey, TValue> : IGrouping<TKey, TValue>
    {
        public TKey Key { get; set; }

        IEnumerator<TValue> IEnumerable<TValue>.GetEnumerator()
        {
            return Enumerable.Empty<TValue>().GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return Enumerable.Empty<TValue>().GetEnumerator();
        }
    }
}
