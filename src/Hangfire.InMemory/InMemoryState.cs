// This file is part of Hangfire.InMemory. Copyright © 2020 Hangfire OÜ.
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
using System.Collections.Concurrent;
using System.Collections.Generic;
using Hangfire.InMemory.Entities;
using Hangfire.Storage;

namespace Hangfire.InMemory
{
    internal sealed class InMemoryState
    {
        private readonly JobStateCreatedAtComparer<string> _jobEntryComparer;

        // State index uses case-insensitive comparisons, despite the current settings. SQL Server
        // uses case-insensitive by default, and Redis doesn't use state index that's based on user values.
        private readonly Dictionary<string, SortedSet<JobEntry<string>>> _jobStateIndex = new Dictionary<string, SortedSet<JobEntry<string>>>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, LockEntry<JobStorageConnection>> _locks;
        private readonly ConcurrentDictionary<string, QueueEntry> _queues;

        private readonly SortedDictionary<string, JobEntry<string>> _jobs;
        private readonly SortedDictionary<string, HashEntry> _hashes;
        private readonly SortedDictionary<string, ListEntry> _lists;
        private readonly SortedDictionary<string, SetEntry> _sets;
        private readonly SortedDictionary<string, CounterEntry> _counters;
        private readonly SortedDictionary<string, ServerEntry> _servers;

        public InMemoryState(InMemoryStorageOptions options)
        {
            Options = options;

            _jobEntryComparer = new JobStateCreatedAtComparer<string>(options.StringComparer);

            _locks = CreateDictionary<LockEntry<JobStorageConnection>>(options.StringComparer);
            _queues = CreateConcurrentDictionary<QueueEntry>(options.StringComparer);

            _jobs = CreateSortedDictionary<JobEntry<string>>(options.StringComparer);
            _hashes = CreateSortedDictionary<HashEntry>(options.StringComparer);
            _lists = CreateSortedDictionary<ListEntry>(options.StringComparer);
            _sets = CreateSortedDictionary<SetEntry>(options.StringComparer);
            _counters = CreateSortedDictionary<CounterEntry>(options.StringComparer);
            _servers = CreateSortedDictionary<ServerEntry>(options.StringComparer);

            var expirableEntryComparer = new ExpirableEntryComparer<string>(options.StringComparer);
            ExpiringJobsIndex = new SortedSet<JobEntry<string>>(expirableEntryComparer);
            ExpiringCountersIndex = new SortedSet<CounterEntry>(expirableEntryComparer);
            ExpiringHashesIndex = new SortedSet<HashEntry>(expirableEntryComparer);
            ExpiringListsIndex = new SortedSet<ListEntry>(expirableEntryComparer);
            ExpiringSetsIndex = new SortedSet<SetEntry>(expirableEntryComparer);
        }

        public InMemoryStorageOptions Options { get; }

        public IDictionary<string, LockEntry<JobStorageConnection>> Locks => _locks;
#if NET451
        public ConcurrentDictionary<string, QueueEntry> Queues => _queues;
#else
        public IReadOnlyDictionary<string, QueueEntry> Queues => _queues;
#endif

        public IDictionary<string, JobEntry<string>> Jobs => _jobs;
        public IDictionary<string, HashEntry> Hashes => _hashes;
        public IDictionary<string, ListEntry> Lists => _lists;
        public IDictionary<string, SetEntry> Sets => _sets;
        public IDictionary<string, CounterEntry> Counters => _counters;
        public IDictionary<string, ServerEntry> Servers => _servers;

        public IReadOnlyDictionary<string, SortedSet<JobEntry<string>>> JobStateIndex => _jobStateIndex;

        // TODO: Hide these indexes from external access for safety reasons
        public SortedSet<JobEntry<string>> ExpiringJobsIndex { get; }
        public SortedSet<CounterEntry> ExpiringCountersIndex { get; }
        public SortedSet<HashEntry> ExpiringHashesIndex { get; }
        public SortedSet<ListEntry> ExpiringListsIndex { get; }
        public SortedSet<SetEntry> ExpiringSetsIndex { get; }

        public QueueEntry QueueGetOrCreate(string name)
        {
            if (!_queues.TryGetValue(name, out var entry))
            {
                entry = _queues.GetOrAdd(name, _ => new QueueEntry());
            }

            return entry;
        }

        public void JobCreate(JobEntry<string> entry, TimeSpan? expireIn, bool ignoreMaxExpirationTime = false)
        {
            _jobs.Add(entry.Key, entry);

            if (EntryExpire<string, JobEntry<string>>(entry, ExpiringJobsIndex, entry.CreatedAt, expireIn, ignoreMaxExpirationTime))
            {
                JobDelete(entry);
            }
        }

        public void JobSetState(JobEntry<string> entry, StateEntry state)
        {
            if (entry.State != null && _jobStateIndex.TryGetValue(entry.State.Name, out var indexEntry))
            {
                indexEntry.Remove(entry);
                if (indexEntry.Count == 0) _jobStateIndex.Remove(entry.State.Name);
            }

            entry.State = state;

            if (!_jobStateIndex.TryGetValue(state.Name, out indexEntry))
            {
                _jobStateIndex.Add(state.Name, indexEntry = new SortedSet<JobEntry<string>>(_jobEntryComparer));
            }

            indexEntry.Add(entry);
        }

        public void JobExpire(JobEntry<string> entry, MonotonicTime? now, TimeSpan? expireIn)
        {
            if (EntryExpire<string, JobEntry<string>>(entry, ExpiringJobsIndex, now, expireIn))
            {
                JobDelete(entry);
            }
        }

        public void JobDelete(JobEntry<string> entry)
        {
            EntryRemove(entry, _jobs, ExpiringJobsIndex);

            if (entry.State?.Name != null && _jobStateIndex.TryGetValue(entry.State.Name, out var stateIndex))
            {
                stateIndex.Remove(entry);
                if (stateIndex.Count == 0) _jobStateIndex.Remove(entry.State.Name);
            }
        }

        public HashEntry HashGetOrAdd(string key)
        {
            if (!_hashes.TryGetValue(key, out var hash))
            {
                _hashes.Add(key, hash = new HashEntry(key, Options.StringComparer));
            }

            return hash;
        }

        public void HashExpire(HashEntry hash, MonotonicTime? now, TimeSpan? expireIn)
        {
            if (EntryExpire<string, HashEntry>(hash, ExpiringHashesIndex, now, expireIn))
            {
                HashDelete(hash);
            }
        }

        public void HashDelete(HashEntry hash)
        {
            EntryRemove(hash, _hashes, ExpiringHashesIndex);
        }

        public SetEntry SetGetOrAdd(string key)
        {
            if (!_sets.TryGetValue(key, out var set))
            {
                _sets.Add(key, set = new SetEntry(key, Options.StringComparer));
            }

            return set;
        }

        public void SetExpire(SetEntry set, MonotonicTime? now, TimeSpan? expireIn)
        {
            if (EntryExpire<string, SetEntry>(set, ExpiringSetsIndex, now, expireIn))
            {
                SetDelete(set);
            }
        }

        public void SetDelete(SetEntry set)
        {
            EntryRemove(set, _sets, ExpiringSetsIndex);
        }

        public ListEntry ListGetOrAdd(string key)
        {
            if (!_lists.TryGetValue(key, out var list))
            {
                _lists.Add(key, list = new ListEntry(key));
            }

            return list;
        }

        public void ListExpire(ListEntry entry, MonotonicTime? now, TimeSpan? expireIn)
        {
            if (EntryExpire<string, ListEntry>(entry, ExpiringListsIndex, now, expireIn))
            {
                ListDelete(entry);
            }
        }

        public void ListDelete(ListEntry list)
        {
            EntryRemove(list, _lists, ExpiringListsIndex);
        }

        public CounterEntry CounterGetOrAdd(string key)
        {
            if (!_counters.TryGetValue(key, out var counter))
            {
                _counters.Add(key, counter = new CounterEntry(key));
            }

            return counter;
        }

        public void CounterExpire(CounterEntry counter, MonotonicTime? now, TimeSpan? expireIn)
        {
            // We don't apply MaxExpirationTime rules for counters, because they
            // usually have fixed size, and because statistics should be kept for
            // days.
            if (EntryExpire<string, CounterEntry>(counter, ExpiringCountersIndex, now, expireIn, ignoreMaxExpirationTime: true))
            {
                CounterDelete(counter);
            }
        }

        public void CounterDelete(CounterEntry entry)
        {
            EntryRemove(entry, _counters, ExpiringCountersIndex);
        }

        public void ServerAdd(string serverId, ServerEntry entry)
        {
            _servers.Add(serverId, entry);
        }

        public void ServerRemove(string serverId)
        {
            _servers.Remove(serverId);
        }

        public void EvictExpiredEntries(MonotonicTime now)
        {
            EvictFromIndex<string, JobEntry<string>>(now, ExpiringJobsIndex, JobDelete);
            EvictFromIndex<string, HashEntry>(now, ExpiringHashesIndex, HashDelete);
            EvictFromIndex<string, ListEntry>(now, ExpiringListsIndex, ListDelete);
            EvictFromIndex<string, SetEntry>(now, ExpiringSetsIndex, SetDelete);
            EvictFromIndex<string, CounterEntry>(now, ExpiringCountersIndex, CounterDelete);
        }

        private static void EvictFromIndex<TKey, TEntry>(MonotonicTime now, SortedSet<TEntry> index, Action<TEntry> action)
            where TEntry : IExpirableEntry<TKey>
        {
            TEntry entry;

            while (index.Count > 0 && (entry = index.Min).ExpireAt.HasValue && now >= entry.ExpireAt)
            {
                action(entry);
            }
        }

        private static void EntryRemove<TKey, TEntry>(TEntry entry, IDictionary<TKey, TEntry> index, ISet<TEntry> expirationIndex)
            where TEntry : IExpirableEntry<TKey>
        {
            index.Remove(entry.Key);

            if (entry.ExpireAt.HasValue)
            {
                expirationIndex.Remove(entry);
            }
        }

        private bool EntryExpire<TKey, TEntry>(TEntry entry, ISet<TEntry> index, MonotonicTime? now, TimeSpan? expireIn, bool ignoreMaxExpirationTime = false)
            where TEntry : IExpirableEntry<TKey>
        {
            if (entry.ExpireAt.HasValue)
            {
                index.Remove(entry);
            }

            if (now.HasValue && expireIn.HasValue)
            {
                if (!ignoreMaxExpirationTime && Options.MaxExpirationTime.HasValue && expireIn > Options.MaxExpirationTime)
                {
                    expireIn = Options.MaxExpirationTime;
                }

                if (expireIn <= TimeSpan.Zero)
                {
                    entry.ExpireAt = null; // Expiration Index doesn't contain this entry
                    return true;
                }

                entry.ExpireAt = now.Value.Add(expireIn.Value);
                index.Add(entry);
                return false;
            }

            entry.ExpireAt = null;
            return false;
        }

        private static Dictionary<string, T> CreateDictionary<T>(StringComparer comparer)
        {
            return new Dictionary<string, T>(comparer);
        }

        private static SortedDictionary<string, T> CreateSortedDictionary<T>(StringComparer comparer)
        {
            return new SortedDictionary<string, T>(comparer);
        }

        private static ConcurrentDictionary<string, T> CreateConcurrentDictionary<T>(StringComparer comparer)
        {
            return new ConcurrentDictionary<string, T>(comparer);
        }
    }
}