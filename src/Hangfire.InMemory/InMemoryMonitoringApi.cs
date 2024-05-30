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
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Hangfire.Annotations;
using Hangfire.Common;
using Hangfire.InMemory.State;
using Hangfire.States;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;

namespace Hangfire.InMemory
{
    internal sealed class InMemoryMonitoringApi<TKey> : JobStorageMonitor
        where TKey : IComparable<TKey>
    {
        private readonly DispatcherBase<TKey> _dispatcher;
        private readonly IKeyProvider<TKey> _keyProvider;

        public InMemoryMonitoringApi([NotNull] DispatcherBase<TKey> dispatcher, [NotNull] IKeyProvider<TKey> keyProvider)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _keyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
        }

        public override IList<QueueWithTopEnqueuedJobsDto> Queues()
        {
            return QueryMonitoringAndWait(state =>
            {
                var result = new List<QueueWithTopEnqueuedJobsDto>();

                foreach (var queueEntry in state.Queues)
                {
                    var queueResult = new JobList<EnqueuedJobDto>(Enumerable.Empty<KeyValuePair<string, EnqueuedJobDto>>());
                    var index = 0;
                    const int count = 5;

                    foreach (var message in queueEntry.Value.Queue)
                    {
                        if (index++ >= count) break;

                        Job job = null;
                        JobLoadException loadException = null;

                        if (state.Jobs.TryGetValue(message, out var jobEntry))
                        {
                            job = jobEntry.InvocationData.TryGetJob(out loadException);
                        }

                        var stateName = jobEntry?.State?.Name;
                        var inEnqueuedState = EnqueuedState.StateName.Equals(
                            stateName,
                            StringComparison.OrdinalIgnoreCase);

                        queueResult.Add(new KeyValuePair<string, EnqueuedJobDto>(_keyProvider.ToString(message), new EnqueuedJobDto
                        {
                            Job = job,
                            LoadException = loadException,
                            InvocationData = jobEntry?.InvocationData,
                            State = stateName,
                            InEnqueuedState = inEnqueuedState,
                            EnqueuedAt = inEnqueuedState ? jobEntry?.State?.CreatedAt.ToUtcDateTime() : null,
                            StateData = inEnqueuedState && jobEntry?.State != null
                                ? jobEntry.State.Data?.ToDictionary(x => x.Key, x => x.Value, state.StringComparer)
                                : null
                        }));
                    }

                    result.Add(new QueueWithTopEnqueuedJobsDto
                    {
                        Length = queueEntry.Value.Queue.Count,
                        Name = queueEntry.Key,
                        FirstJobs = queueResult
                    });
                }

                return result.OrderBy(x => x.Name, state.StringComparer).ToList();
            });
        }

        public override IList<ServerDto> Servers()
        {
            return QueryMonitoringAndWait(state =>
            {
                var result = new List<ServerDto>(state.Servers.Count);

                foreach (var entry in state.Servers)
                {
                    result.Add(new ServerDto
                    {
                        Name = entry.Key,
                        Queues = entry.Value.Context.Queues.ToArray(),
                        WorkersCount = entry.Value.Context.WorkerCount,
                        Heartbeat = entry.Value.HeartbeatAt.ToUtcDateTime(),
                        StartedAt = entry.Value.StartedAt.ToUtcDateTime(),
                    });
                }

                return result.OrderBy(x => x.Name, state.StringComparer).ToList();
            });
        }

        public override JobDetailsDto JobDetails([NotNull] string jobId)
        {
            if (jobId == null) throw new ArgumentNullException(nameof(jobId));

            if (!_keyProvider.TryParse(jobId, out var jobKey))
            {
                return null;
            }

            return QueryMonitoringAndWait(state =>
            {
                if (!state.Jobs.TryGetValue(jobKey, out var entry))
                {
                    return null;
                }

                var job = entry.InvocationData.TryGetJob(out var loadException);

                return new JobDetailsDto
                {
                    CreatedAt = entry.CreatedAt.ToUtcDateTime(),
                    ExpireAt = entry.ExpireAt?.ToUtcDateTime(),
                    Job = job,
                    InvocationData = entry.InvocationData,
                    LoadException = loadException,
                    Properties = entry.GetParameters().ToDictionary(x => x.Key, x => x.Value, state.StringComparer),
                    History = entry.History.Select(x => new StateHistoryDto
                    {
                        CreatedAt = x.CreatedAt.ToUtcDateTime(),
                        StateName = x.Name,
                        Reason = x.Reason,
                        Data = x.Data?.ToDictionary(d => d.Key, d => d.Value, state.StringComparer)
                    }).Reverse().ToList()
                };
            });
        }

        public override StatisticsDto GetStatistics()
        {
            return QueryMonitoringAndWait(state => new StatisticsDto
            {
                Enqueued = GetCountByStateName(EnqueuedState.StateName, state),
                Scheduled = GetCountByStateName(ScheduledState.StateName, state),
                Processing = GetCountByStateName(ProcessingState.StateName, state),
                Failed = GetCountByStateName(FailedState.StateName, state),
                Succeeded = state.Counters.TryGetValue("stats:succeeded", out var succeeded) ? succeeded.Value : 0,
                Deleted = state.Counters.TryGetValue("stats:deleted", out var deleted) ? deleted.Value : 0,
                Queues = state.Queues.Count,
                Servers = state.Servers.Count,
                Recurring = state.Sets.TryGetValue("recurring-jobs", out var recurring)
                    ? recurring.Count
                    : 0,
                Retries = state.Sets.TryGetValue("retries", out var retries)
                    ? retries.Count
                    : 0,
                Awaiting = state.JobStateIndex.TryGetValue(AwaitingState.StateName, out var indexEntry)
                    ? indexEntry.Count
                    : 0,
            });
        }

        public override JobList<EnqueuedJobDto> EnqueuedJobs([NotNull] string queueName, int from, int perPage)
        {
            if (queueName == null) throw new ArgumentNullException(nameof(queueName));

            return QueryMonitoringAndWait(state =>
            {
                var result = new JobList<EnqueuedJobDto>(Enumerable.Empty<KeyValuePair<string, EnqueuedJobDto>>());

                if (state.Queues.TryGetValue(queueName, out var queue))
                {
                    var counter = 0;

                    foreach (var message in queue.Queue)
                    {
                        if (counter < from) { counter++; continue; }
                        if (counter >= from + perPage) break;

                        Job job = null;
                        JobLoadException loadException = null;

                        if (state.Jobs.TryGetValue(message, out var jobEntry))
                        {
                            job = jobEntry.InvocationData.TryGetJob(out loadException);
                        }

                        var stateName = jobEntry?.State?.Name;
                        var inEnqueuedState = EnqueuedState.StateName.Equals(
                            stateName,
                            StringComparison.OrdinalIgnoreCase);

                        result.Add(new KeyValuePair<string, EnqueuedJobDto>(_keyProvider.ToString(message), new EnqueuedJobDto
                        {
                            Job = job,
                            InvocationData = jobEntry?.InvocationData,
                            LoadException = loadException,
                            State = stateName,
                            InEnqueuedState = inEnqueuedState,
                            EnqueuedAt = inEnqueuedState ? jobEntry?.State?.CreatedAt.ToUtcDateTime() : null,
                            StateData = inEnqueuedState && jobEntry?.State != null
                                ? jobEntry.State.Data?.ToDictionary(x => x.Key, x => x.Value, state.StringComparer)
                                : null
                        }));

                        counter++;
                    }
                }

                return result;
            });
        }

        public override JobList<FetchedJobDto> FetchedJobs([NotNull] string queueName, int from, int perPage)
        {
            if (queueName == null) throw new ArgumentNullException(nameof(queueName));
            return new JobList<FetchedJobDto>(Enumerable.Empty<KeyValuePair<string, FetchedJobDto>>());
        }

        public override JobList<ProcessingJobDto> ProcessingJobs(int from, int count)
        {
            return QueryMonitoringAndWait(state =>
            {
                var result = new JobList<ProcessingJobDto>(Enumerable.Empty<KeyValuePair<string, ProcessingJobDto>>());
                if (state.JobStateIndex.TryGetValue(ProcessingState.StateName, out var indexEntry))
                {
                    var index = 0;

                    foreach (var entry in indexEntry)
                    {
                        if (index < from) { index++; continue; }
                        if (index >= from + count) break;

                        var job = entry.InvocationData.TryGetJob(out var loadException);
                        var inProcessingState = ProcessingState.StateName.Equals(
                            entry.State?.Name,
                            StringComparison.OrdinalIgnoreCase);
                        var data = entry.State?.Data?.ToDictionary(x => x.Key, x => x.Value, state.StringComparer);

                        result.Add(new KeyValuePair<string, ProcessingJobDto>(_keyProvider.ToString(entry.Key), new ProcessingJobDto
                        {
                            ServerId = data?.ContainsKey("ServerId") ?? false ? data["ServerId"] : null,
                            Job = job,
                            InvocationData = entry.InvocationData,
                            LoadException = loadException,
                            InProcessingState = inProcessingState,
                            StartedAt = entry.State?.CreatedAt.ToUtcDateTime(),
                            StateData = inProcessingState && data != null
                                ? data
                                : null
                        }));

                        index++;
                    }
                }

                return result;
            });
        }

        public override JobList<ScheduledJobDto> ScheduledJobs(int from, int count)
        {
            return QueryMonitoringAndWait(state =>
            {
                var result = new JobList<ScheduledJobDto>(Enumerable.Empty<KeyValuePair<string, ScheduledJobDto>>());
                if (state.JobStateIndex.TryGetValue(ScheduledState.StateName, out var indexEntry))
                {
                    var index = 0;

                    foreach (var entry in indexEntry)
                    {
                        if (index < from) { index++; continue; }
                        if (index >= from + count) break;

                        var job = entry.InvocationData.TryGetJob(out var loadException);
                        
                        var inScheduledState = ScheduledState.StateName.Equals(
                            entry.State?.Name,
                            StringComparison.OrdinalIgnoreCase);

                        result.Add(new KeyValuePair<string, ScheduledJobDto>(_keyProvider.ToString(entry.Key), new ScheduledJobDto
                        {
                            EnqueueAt = (entry.State?.Data != null && entry.State.Data.ToDictionary(x => x.Key, x => x.Value, state.StringComparer).TryGetValue("EnqueueAt", out var enqueueAt)
                                ? JobHelper.DeserializeNullableDateTime(enqueueAt)
                                : null) ?? DateTime.MinValue,
                            Job = job,
                            InvocationData = entry.InvocationData,
                            LoadException = loadException,
                            InScheduledState = inScheduledState,
                            ScheduledAt = entry.State?.CreatedAt.ToUtcDateTime(),
                            StateData = inScheduledState && entry.State != null
                                ? entry.State.Data?.ToDictionary(x => x.Key, x => x.Value, state.StringComparer)
                                : null
                        }));

                        index++;
                    }
                }

                return result;
            });
        }

        public override JobList<SucceededJobDto> SucceededJobs(int from, int count)
        {
            return QueryMonitoringAndWait(state =>
            {
                var result = new JobList<SucceededJobDto>(Enumerable.Empty<KeyValuePair<string, SucceededJobDto>>());
                if (state.JobStateIndex.TryGetValue(SucceededState.StateName, out var indexEntry))
                {
                    var index = 0;

                    foreach (var entry in indexEntry.Reverse())
                    {
                        if (index < from) { index++; continue; }
                        if (index >= from + count) break;

                        var job = entry.InvocationData.TryGetJob(out var loadException);
                        var inSucceededState = SucceededState.StateName.Equals(
                            entry.State?.Name,
                            StringComparison.OrdinalIgnoreCase);
                        var data = entry.State?.Data?.ToDictionary(x => x.Key, x => x.Value, state.StringComparer);

                        result.Add(new KeyValuePair<string, SucceededJobDto>(_keyProvider.ToString(entry.Key), new SucceededJobDto
                        {
                            Result = data?.ContainsKey("Result") ?? false ? data["Result"] : null,
                            TotalDuration = (data?.ContainsKey("PerformanceDuration") ?? false) && (data?.ContainsKey("Latency") ?? false) 
                                ? long.Parse(data["PerformanceDuration"], CultureInfo.InvariantCulture) + long.Parse(data["Latency"], CultureInfo.InvariantCulture)
                                : (long?)null,
                            Job = job,
                            InvocationData = entry.InvocationData,
                            LoadException = loadException,
                            InSucceededState = inSucceededState,
                            SucceededAt = entry.State?.CreatedAt.ToUtcDateTime(),
                            StateData = inSucceededState && data != null
                                ? data
                                : null
                        }));

                        index++;
                    }
                }

                return result;
            });
        }

        public override JobList<FailedJobDto> FailedJobs(int from, int count)
        {
            return QueryMonitoringAndWait(state =>
            {
                var result = new JobList<FailedJobDto>(Enumerable.Empty<KeyValuePair<string, FailedJobDto>>());
                if (state.JobStateIndex.TryGetValue(FailedState.StateName, out var indexEntry))
                {
                    var index = 0;

                    foreach (var entry in indexEntry.Reverse())
                    {
                        if (index < from) { index++; continue; }
                        if (index >= from + count) break;

                        var job = entry.InvocationData.TryGetJob(out var loadException);
                        var inFailedState = FailedState.StateName.Equals(
                            entry.State?.Name,
                            StringComparison.OrdinalIgnoreCase);
                        var data = entry.State?.Data?.ToDictionary(x => x.Key, x => x.Value, state.StringComparer);

                        result.Add(new KeyValuePair<string, FailedJobDto>(_keyProvider.ToString(entry.Key), new FailedJobDto
                        {
                            Job = job,
                            InvocationData = entry.InvocationData,
                            LoadException = loadException,
                            ExceptionDetails = data?.ContainsKey("ExceptionDetails") ?? false ? data["ExceptionDetails"] : null,
                            ExceptionType = data?.ContainsKey("ExceptionType") ?? false ? data["ExceptionType"] : null,
                            ExceptionMessage = data?.ContainsKey("ExceptionMessage") ?? false ? data["ExceptionMessage"] : null,
                            Reason = entry.State?.Reason,
                            InFailedState = inFailedState,
                            FailedAt = entry.State?.CreatedAt.ToUtcDateTime(),
                            StateData = inFailedState && data != null
                                ? data
                                : null
                        }));

                        index++;
                    }
                }

                return result;
            });
        }

        public override JobList<DeletedJobDto> DeletedJobs(int from, int count)
        {
            return QueryMonitoringAndWait(state =>
            {
                var result = new JobList<DeletedJobDto>(Enumerable.Empty<KeyValuePair<string, DeletedJobDto>>());
                if (state.JobStateIndex.TryGetValue(DeletedState.StateName, out var indexEntry))
                {
                    var index = 0;

                    foreach (var entry in indexEntry.Reverse())
                    {
                        if (index < from) { index++; continue; }
                        if (index >= from + count) break;

                        var job = entry.InvocationData.TryGetJob(out var loadException);
                        var inDeletedState = DeletedState.StateName.Equals(
                            entry.State?.Name,
                            StringComparison.OrdinalIgnoreCase);

                        result.Add(new KeyValuePair<string, DeletedJobDto>(_keyProvider.ToString(entry.Key), new DeletedJobDto
                        {
                            Job = job,
                            InvocationData = entry.InvocationData,
                            LoadException = loadException,
                            InDeletedState = inDeletedState,
                            DeletedAt = entry.State?.CreatedAt.ToUtcDateTime(),
                            StateData = inDeletedState && entry.State != null
                                ? entry.State.Data?.ToDictionary(x => x.Key, x => x.Value, state.StringComparer)
                                : null
                        }));

                        index++;
                    }
                }

                return result;
            });
        }

        public override JobList<AwaitingJobDto> AwaitingJobs(int from, int count)
        {
            return QueryMonitoringAndWait(state =>
            {
                var result = new JobList<AwaitingJobDto>(Enumerable.Empty<KeyValuePair<string, AwaitingJobDto>>());
                if (state.JobStateIndex.TryGetValue(AwaitingState.StateName, out var indexEntry))
                {
                    var index = 0;

                    foreach (var entry in indexEntry)
                    {
                        if (index < from) { index++; continue; }
                        if (index >= from + count) break;

                        var job = entry.InvocationData.TryGetJob(out var loadException);
                        var inAwaitingState = AwaitingState.StateName.Equals(
                            entry.State?.Name,
                            StringComparison.OrdinalIgnoreCase);

                        string parentStateName = null;

                        if (inAwaitingState && entry.State?.Data != null &&
                            entry.State.Data.ToDictionary(x => x.Key, x => x.Value, state.StringComparer).TryGetValue("ParentId", out var parentId) &&
                            _keyProvider.TryParse(parentId, out var parentKey) &&
                            state.Jobs.TryGetValue(parentKey, out var parentEntry))
                        {
                            parentStateName = parentEntry.State?.Name;
                        }

                        result.Add(new KeyValuePair<string, AwaitingJobDto>(_keyProvider.ToString(entry.Key), new AwaitingJobDto
                        {
                            Job = job,
                            InvocationData = entry.InvocationData,
                            LoadException = loadException,
                            InAwaitingState = inAwaitingState,
                            AwaitingAt = entry.State?.CreatedAt.ToUtcDateTime(),
                            StateData = inAwaitingState && entry.State != null
                                ? entry.State.Data?.ToDictionary(x => x.Key, x => x.Value, state.StringComparer)
                                : null,
                            ParentStateName = parentStateName
                        }));

                        index++;
                    }
                }

                return result;
            });
        }

        public override long ScheduledCount()
        {
            return GetCountByStateName(ScheduledState.StateName);
        }

        public override long EnqueuedCount([NotNull] string queueName)
        {
            if (queueName == null) throw new ArgumentNullException(nameof(queueName));

            return QueryMonitoringValueAndWait(state => state.Queues.TryGetValue(queueName, out var queue) 
                ? queue.Queue.Count
                : 0);
        }

        public override long FetchedCount([NotNull] string queue)
        {
            if (queue == null) throw new ArgumentNullException(nameof(queue));
            return 0;
        }

        public override long FailedCount()
        {
            return GetCountByStateName(FailedState.StateName);
        }

        public override long ProcessingCount()
        {
            return GetCountByStateName(ProcessingState.StateName);
        }

        public override long SucceededListCount()
        {
            return GetCountByStateName(SucceededState.StateName);
        }

        public override long DeletedListCount()
        {
            return GetCountByStateName(DeletedState.StateName);
        }

        public override long AwaitingCount()
        {
            return GetCountByStateName(AwaitingState.StateName);
        }

        public override IDictionary<DateTime, long> SucceededByDatesCount()
        {
            var now = _dispatcher.GetMonotonicTime();
            return QueryMonitoringAndWait(state => GetTimelineStats(state, now, "succeeded"));
        }

        public override IDictionary<DateTime, long> FailedByDatesCount()
        {
            var now = _dispatcher.GetMonotonicTime();
            return QueryMonitoringAndWait(state => GetTimelineStats(state, now, "failed"));
        }

        public override IDictionary<DateTime, long> DeletedByDatesCount()
        {
            var now = _dispatcher.GetMonotonicTime();
            return QueryMonitoringAndWait(state => GetTimelineStats(state, now, "deleted"));
        }

        public override IDictionary<DateTime, long> HourlySucceededJobs()
        {
            var now = _dispatcher.GetMonotonicTime();
            return QueryMonitoringAndWait(state => GetHourlyTimelineStats(state, now, "succeeded"));
        }

        public override IDictionary<DateTime, long> HourlyFailedJobs()
        {
            var now = _dispatcher.GetMonotonicTime();
            return QueryMonitoringAndWait(state => GetHourlyTimelineStats(state, now, "failed"));
        }

        public override IDictionary<DateTime, long> HourlyDeletedJobs()
        {
            var now = _dispatcher.GetMonotonicTime();
            return QueryMonitoringAndWait(state => GetHourlyTimelineStats(state, now, "deleted"));
        }

        private long GetCountByStateName(string stateName)
        {
            return QueryMonitoringValueAndWait(state => GetCountByStateName(stateName, state));
        }

        private static int GetCountByStateName(string stateName, MemoryState<TKey> state)
        {
            if (state.JobStateIndex.TryGetValue(stateName, out var index))
            {
                return index.Count;
            }

            return 0;
        }

        private static Dictionary<DateTime, long> GetHourlyTimelineStats(MemoryState<TKey> state, MonotonicTime now, string type)
        {
            var endDate = now.ToUtcDateTime();
            var dates = new List<DateTime>();
            for (var i = 0; i < 24; i++)
            {
                dates.Add(endDate);
                endDate = endDate.AddHours(-1);
            }

            var keys = dates.Select(x => $"stats:{type}:{x:yyyy-MM-dd-HH}").ToArray();
            var valuesMap = keys.Select(key => state.Counters.TryGetValue(key, out var entry) ? entry.Value : 0).ToArray();

            var result = new Dictionary<DateTime, long>();
            for (var i = 0; i < dates.Count; i++)
            {
                result.Add(dates[i], valuesMap[i]);
            }

            return result;
        }

        private static Dictionary<DateTime, long> GetTimelineStats(MemoryState<TKey> state, MonotonicTime now, string type)
        {
            var endDate = now.ToUtcDateTime().Date;
            var startDate = endDate.AddDays(-7);
            var dates = new List<DateTime>();

            while (startDate < endDate)
            {
                dates.Add(endDate);
                endDate = endDate.AddDays(-1);
            }

            var stringDates = dates.Select(x => x.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToList();
            var keys = stringDates.Select(x => $"stats:{type}:{x}").ToArray();
            var valuesMap = keys.Select(key => state.Counters.TryGetValue(key, out var entry) ? entry.Value : 0).ToArray();

            var result = new Dictionary<DateTime, long>();
            for (var i = 0; i < stringDates.Count; i++)
            {
                result.Add(dates[i], valuesMap[i]);
            }

            return result;
        }

        private T QueryMonitoringAndWait<T>(Func<MemoryState<TKey>, T> callback)
            where T : class
        {
            return _dispatcher.QueryReadAndWait(new MonitoringQuery<T>(callback));
        }

        private T QueryMonitoringValueAndWait<T>(Func<MemoryState<TKey>, T> callback)
            where T : struct
        {
            return _dispatcher.QueryReadAndWait(new MonitoringValueQuery<T>(callback));
        }

        private sealed class MonitoringQuery<T>(Func<MemoryState<TKey>, T> callback) : Command<TKey, T>
            where T : class
        {
            protected override T Execute(MemoryState<TKey> state)
            {
                return callback(state);
            }
        }

        private sealed class MonitoringValueQuery<T>(Func<MemoryState<TKey>, T> callback) : ValueCommand<TKey, T>
            where T : struct
        {
            protected override T Execute(MemoryState<TKey> state)
            {
                return callback(state);
            }
        }
    }
}