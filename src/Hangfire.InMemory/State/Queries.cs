﻿// This file is part of Hangfire.InMemory. Copyright © 2024 Hangfire OÜ.
// 
// Hangfire is free software: you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License as 
// published by the Free Software Foundation, either version 3 
// of the License, or any later version.
// 
// Hangfire is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU Lesser General Public License for more details.
// 
// You should have received a copy of the GNU Lesser General Public 
// License along with Hangfire. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Linq;
using Hangfire.Storage;

namespace Hangfire.InMemory.State
{
    internal static class Queries
    {
        public sealed class JobGetData<TKey>(TKey key) : Command<TKey, JobGetData<TKey>.Data?>
            where TKey : IComparable<TKey>
        {
            protected override Data? Execute(MemoryState<TKey> state)
            {
                if (!state.Jobs.TryGetValue(key, out var entry))
                {
                    return null;
                }

                return new Data(
                    entry.InvocationData,
                    entry.State?.Name,
                    entry.CreatedAt,
                    entry.GetParameters(),
                    state.StringComparer);
            }

            public sealed class Data(
                InvocationData invocationData,
                string? state,
                MonotonicTime createdAt,
                KeyValuePair<string, string>[] parameters,
                StringComparer stringComparer)
            {
                public InvocationData InvocationData { get; } = invocationData;
                public string? State { get; } = state;
                public MonotonicTime CreatedAt { get; } = createdAt;
                public KeyValuePair<string, string>[] Parameters { get; } = parameters;
                public StringComparer StringComparer { get; } = stringComparer;
            }
        }

        public sealed class JobGetState<TKey>(TKey key) : Command<TKey, JobGetState<TKey>.Data?>
            where TKey : IComparable<TKey>
        {
            protected override Data? Execute(MemoryState<TKey> state)
            {
                if (!state.Jobs.TryGetValue(key, out var entry) || entry.State == null)
                {
                    return null;
                }

                return new Data(entry.State.Name, entry.State.Reason, entry.State.Data, state.StringComparer);
            }
            
            public sealed class Data(
                string name,
                string reason,
                KeyValuePair<string, string>[] stateData,
                StringComparer stringComparer)
            {
                public string Name { get; } = name;
                public string Reason { get; } = reason;
                public KeyValuePair<string, string>[] StateData { get; } = stateData;
                public StringComparer StringComparer { get; } = stringComparer;
            }
        }

        public sealed class JobGetParameter<TKey>(TKey key, string name) : Command<TKey, string?>
            where TKey : IComparable<TKey>
        {
            protected override string? Execute(MemoryState<TKey> state)
            {
                return state.Jobs.TryGetValue(key, out var entry)
                    ? entry.GetParameter(name, state.StringComparer)
                    : null;
            }
        }

        public sealed class SortedSetGetAll<TKey>(string key) : Command<TKey, HashSet<string>>
            where TKey : IComparable<TKey>
        {
            protected override HashSet<string> Execute(MemoryState<TKey> state)
            {
                var result = new HashSet<string>(state.StringComparer);

                if (state.Sets.TryGetValue(key, out var entry))
                {
                    foreach (var item in entry)
                    {
                        result.Add(item.Value);
                    }
                }

                return result;
            }
        }

        public sealed class SortedSetFirstByLowestScore<TKey>(string key, double fromScore, double toScore) 
            : Command<TKey, string?>
            where TKey : IComparable<TKey>
        {
            protected override string? Execute(MemoryState<TKey> state)
            {
                if (state.Sets.TryGetValue(key, out var entry))
                {
                    return entry.GetFirstBetween(fromScore, toScore);
                }

                return null;
            }
        }

        public sealed class SortedSetFirstByLowestScoreMultiple<TKey>(string key, double fromScore, double toScore, int count) 
            : Command<TKey, List<string>>
            where TKey : IComparable<TKey>
        {
            protected override List<string> Execute(MemoryState<TKey> state)
            {
                if (state.Sets.TryGetValue(key, out var entry))
                {
                    return entry.GetViewBetween(fromScore, toScore, count);
                }

                return new List<string>();
            }
        }

        public sealed class SortedSetRange<TKey>(string key, int startingFrom, int endingAt) : Command<TKey, List<string>>
            where TKey : IComparable<TKey>
        {
            protected override List<string> Execute(MemoryState<TKey> state)
            {
                var result = new List<string>();

                if (state.Sets.TryGetValue(key, out var entry))
                {
                    var counter = 0;

                    foreach (var item in entry)
                    {
                        if (counter < startingFrom) { counter++; continue; }
                        if (counter > endingAt) break;

                        result.Add(item.Value);

                        counter++;
                    }
                }

                return result;
            }
        }

        public sealed class SortedSetContains<TKey>(string key, string value) : ValueCommand<TKey, bool>
            where TKey : IComparable<TKey>
        {
            protected override bool Execute(MemoryState<TKey> state)
            {
                return state.Sets.TryGetValue(key, out var entry) && entry.Contains(value);
            }
        }

        public sealed class SortedSetCount<TKey>(string key) : ValueCommand<TKey, int>
            where TKey : IComparable<TKey>
        {
            protected override int Execute(MemoryState<TKey> state)
            {
                return state.Sets.TryGetValue(key, out var entry) ? entry.Count : 0;
            }
        }

        public sealed class SortedSetCountMultiple<TKey>(IEnumerable<string> keys, int limit) : ValueCommand<TKey, int>
            where TKey : IComparable<TKey>
        {
            protected override int Execute(MemoryState<TKey> state)
            {
                var count = 0;

                foreach (var key in keys)
                {
                    if (count >= limit) break;
                    count += state.Sets.TryGetValue(key, out var entry) ? entry.Count : 0;
                }

                return Math.Min(count, limit);
            }
        }

        public sealed class SortedSetTimeToLive<TKey>(string key) : ValueCommand<TKey, MonotonicTime?>
            where TKey : IComparable<TKey>
        {
            protected override MonotonicTime? Execute(MemoryState<TKey> state)
            {
                if (state.Sets.TryGetValue(key, out var entry) && entry.ExpireAt.HasValue)
                {
                    return entry.ExpireAt;
                }

                return null;
            }
        }

        public sealed class HashGetAll<TKey>(string key) : Command<TKey, Dictionary<string, string>?>
            where TKey : IComparable<TKey>
        {
            protected override Dictionary<string, string>? Execute(MemoryState<TKey> state)
            {
                if (state.Hashes.TryGetValue(key, out var entry))
                {
                    return entry.Value.ToDictionary(static x => x.Key, static x => x.Value, state.StringComparer);
                }

                return null;
            }
        }

        public sealed class HashGet<TKey>(string key, string name) : Command<TKey, string?>
            where TKey : IComparable<TKey>
        {
            protected override string? Execute(MemoryState<TKey> state)
            {
                if (state.Hashes.TryGetValue(key, out var entry) && entry.Value.TryGetValue(name, out var result))
                {
                    return result;
                }

                return null;
            }
        }

        public sealed class HashFieldCount<TKey>(string key) : ValueCommand<TKey, int>
            where TKey : IComparable<TKey>
        {
            protected override int Execute(MemoryState<TKey> state)
            {
                return state.Hashes.TryGetValue(key, out var entry) ? entry.Value.Count : 0;
            }
        }

        public sealed class HashTimeToLive<TKey>(string key) : ValueCommand<TKey, MonotonicTime?>
            where TKey : IComparable<TKey>
        {
            protected override MonotonicTime? Execute(MemoryState<TKey> state)
            {
                if (state.Hashes.TryGetValue(key, out var entry) && entry.ExpireAt.HasValue)
                {
                    return entry.ExpireAt;
                }

                return null;
            }
        }

        public sealed class ListGetAll<TKey>(string key) : Command<TKey, List<string>>
            where TKey : IComparable<TKey>
        {
            protected override List<string> Execute(MemoryState<TKey> state)
            {
                if (state.Lists.TryGetValue(key, out var entry))
                {
                    return new List<string>(entry);
                }

                return new List<string>();
            }
        }

        public sealed class ListRange<TKey>(string key, int startingFrom, int endingAt) : Command<TKey, List<string>>
            where TKey : IComparable<TKey>
        {
            protected override List<string> Execute(MemoryState<TKey> state)
            {
                var result = new List<string>();

                if (state.Lists.TryGetValue(key, out var entry))
                {
                    var count = endingAt - startingFrom + 1;
                    foreach (var item in entry)
                    {
                        if (startingFrom-- > 0) continue;
                        if (count-- == 0) break;

                        result.Add(item);
                    }
                }

                return result;
            }
        }

        public sealed class ListCount<TKey>(string key) : ValueCommand<TKey, int>
            where TKey : IComparable<TKey>
        {
            protected override int Execute(MemoryState<TKey> state)
            {
                return state.Lists.TryGetValue(key, out var entry) ? entry.Count : 0;
            }
        }

        public sealed class ListTimeToLive<TKey>(string key) : ValueCommand<TKey, MonotonicTime?>
            where TKey : IComparable<TKey>
        {
            protected override MonotonicTime? Execute(MemoryState<TKey> state)
            {
                if (state.Lists.TryGetValue(key, out var entry) && entry.ExpireAt.HasValue)
                {
                    return entry.ExpireAt;
                }

                return null;
            }
        }

        public sealed class CounterGet<TKey>(string key) : ValueCommand<TKey, long>
            where TKey : IComparable<TKey>
        {
            protected override long Execute(MemoryState<TKey> state)
            {
                return state.Counters.TryGetValue(key, out var entry) ? entry.Value : 0;
            }
        }
    }
}