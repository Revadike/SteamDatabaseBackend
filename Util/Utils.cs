/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using SteamKit2;

namespace SteamDatabaseBackend
{
    public static class Utils
    {
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

        public static SteamApps.PICSRequest NewPICSRequest(uint id, ulong accessToken = 0)
        {
            return new SteamApps.PICSRequest(id, accessToken, false);
        }
    }

    public class EmptyGrouping<TKey, TValue> : IGrouping<TKey, TValue>
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
