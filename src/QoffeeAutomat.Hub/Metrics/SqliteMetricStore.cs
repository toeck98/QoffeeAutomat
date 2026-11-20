using System.Globalization;
using Microsoft.Data.Sqlite;
using QoffeeAutomat.Hub.Storage;

namespace QoffeeAutomat.Hub.Metrics;

/// <summary>
/// The metric store on <c>hub.db</c> (PLAN.md D-032, S20 "Metrics on the hub", T910). Numeric and boolean metrics go to <c>metric_raw</c> and are rolled up to
/// <c>metric_rollup_5m</c> and <c>metric_rollup_1h</c>; string, enum and timestamp metrics go to <c>metric_states</c> as a change log. All tables belong to
/// <c>metric_series</c>, which belongs to the agent: removing the agent removes its series and their data (foreign keys with <c>ON DELETE CASCADE</c>).
/// <para>
/// Time is unix milliseconds. A roll-up bucket is identified by its start; the 5 minute buckets are computed from the raw samples, the one hour buckets from the 5 minute
/// buckets (a weighted average, which equals the average of the raw samples), so raw samples may expire long before the hour is rolled up. The roll-ups keep a watermark
/// in <c>hub_meta</c>: everything before it is rolled up. A sample that arrives for a bucket behind the watermark stays raw and is not rolled up.
/// </para>
/// <para>
/// All work runs under the lock of <see cref="HubDatabase"/>, which also guards the caches of series ids and last texts. Statements are constants; values travel as
/// parameters.
/// </para>
/// </summary>
public sealed class SqliteMetricStore : IMetricStore
{
    private const int ConstraintErrorCode = 19;
    private const string Rollup5mThroughKey = "metrics.rollup_5m_through";
    private const string Rollup1hThroughKey = "metrics.rollup_1h_through";

    private const string SelectRollup5m =
        "SELECT bucket, min_value, avg_value, max_value, sample_count FROM metric_rollup_5m WHERE series_id = @series AND bucket >= @from AND bucket <= @to ORDER BY bucket LIMIT @limit;";

    private const string SelectRollup1h =
        "SELECT bucket, min_value, avg_value, max_value, sample_count FROM metric_rollup_1h WHERE series_id = @series AND bucket >= @from AND bucket <= @to ORDER BY bucket LIMIT @limit;";

    private readonly HubDatabase _database;
    private readonly HubMetricsOptions _options;
    private readonly Dictionary<(string Agent, string Metric), SeriesRef> _series = [];
    private readonly Dictionary<long, string?> _lastText = [];

    /// <summary>Creates the store.</summary>
    /// <param name="database">The hub database; it is migrated to the schema that has the metric tables.</param>
    /// <param name="options">The retention and the limits.</param>
    public SqliteMetricStore(HubDatabase database, HubMetricsOptions options)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);

        _database = database;
        _options = options;
    }

    private sealed record SeriesRef(long Id, string ValueType, string? Unit);

    private sealed record SeriesRow(long Id, string MetricId, string ValueType, string? Unit);

    // ------------------------------------------------------------------
    // Writing
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public Task<MetricAppendResult> AppendAsync(IReadOnlyList<MetricSample> samples, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (samples.Count == 0)
        {
            return Task.FromResult(new MetricAppendResult(0, 0, 0));
        }

        return _database.QueryAsync(connection => Append(connection, samples), cancellationToken);
    }

    private MetricAppendResult Append(SqliteConnection connection, IReadOnlyList<MetricSample> samples)
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        try
        {
            int stored = 0;
            int unchanged = 0;
            int skipped = 0;
            using SqliteCommand rawInsert = HubSql.Command(
                connection,
                transaction,
                "INSERT OR REPLACE INTO metric_raw (series_id, ts, value) VALUES (@series, @ts, @value);",
                ("@series", 0L),
                ("@ts", 0L),
                ("@value", 0.0));
            using SqliteCommand stateInsert = HubSql.Command(
                connection,
                transaction,
                "INSERT OR REPLACE INTO metric_states (series_id, ts, value) VALUES (@series, @ts, @value);",
                ("@series", 0L),
                ("@ts", 0L),
                ("@value", string.Empty));

            foreach (IGrouping<string, MetricSample> group in samples.GroupBy(sample => sample.AgentId, StringComparer.Ordinal))
            {
                (int Stored, int Unchanged)? written = null;

                // The agent may have been removed since the hub cached its series: the foreign key refuses the rows. Forget what is cached and try once more; when the
                // agent is really gone, series cannot be created either and its samples are skipped.
                for (int attempt = 0; attempt < 2 && written is null; attempt++)
                {
                    HubSql.Execute(connection, transaction, "SAVEPOINT metric_agent;");
                    try
                    {
                        written = AppendAgent(connection, transaction, group, rawInsert, stateInsert);
                        HubSql.Execute(connection, transaction, "RELEASE metric_agent;");
                    }
                    catch (SqliteException exception) when (exception.SqliteErrorCode == ConstraintErrorCode)
                    {
                        HubSql.Execute(connection, transaction, "ROLLBACK TO metric_agent;");
                        HubSql.Execute(connection, transaction, "RELEASE metric_agent;");
                        ForgetAgent(group.Key);
                    }
                }

                if (written is { } counts)
                {
                    stored += counts.Stored;
                    unchanged += counts.Unchanged;
                }
                else
                {
                    skipped += group.Count();
                }
            }

            transaction.Commit();
            return new MetricAppendResult(stored, unchanged, skipped);
        }
        catch
        {
            // What the caches learned in this transaction is gone with it.
            _series.Clear();
            _lastText.Clear();
            throw;
        }
    }

    private (int Stored, int Unchanged) AppendAgent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IEnumerable<MetricSample> samples,
        SqliteCommand rawInsert,
        SqliteCommand stateInsert)
    {
        int stored = 0;
        int unchanged = 0;
        foreach (MetricSample sample in samples)
        {
            long seriesId = EnsureSeries(connection, transaction, sample);
            if (sample.IsNumeric)
            {
                rawInsert.Parameters["@series"].Value = seriesId;
                rawInsert.Parameters["@ts"].Value = sample.Ts;
                rawInsert.Parameters["@value"].Value = sample.Number;
                rawInsert.ExecuteNonQuery();
                stored++;
                continue;
            }

            string text = sample.Text!.Length > _options.MaxTextLength ? sample.Text[.._options.MaxTextLength] : sample.Text;
            if (!_lastText.TryGetValue(seriesId, out string? last))
            {
                last = HubSql.Scalar(
                    connection,
                    transaction,
                    "SELECT value FROM metric_states WHERE series_id = @series ORDER BY ts DESC LIMIT 1;",
                    ("@series", seriesId)) as string;
                _lastText[seriesId] = last;
            }

            if (string.Equals(last, text, StringComparison.Ordinal))
            {
                unchanged++;
                continue;
            }

            stateInsert.Parameters["@series"].Value = seriesId;
            stateInsert.Parameters["@ts"].Value = sample.Ts;
            stateInsert.Parameters["@value"].Value = text;
            stateInsert.ExecuteNonQuery();
            _lastText[seriesId] = text;
            stored++;
        }

        return (stored, unchanged);
    }

    private long EnsureSeries(SqliteConnection connection, SqliteTransaction transaction, MetricSample sample)
    {
        (string Agent, string Metric) key = (sample.AgentId, sample.MetricId);
        if (_series.TryGetValue(key, out SeriesRef? known)
            && string.Equals(known.ValueType, sample.ValueType, StringComparison.Ordinal)
            && string.Equals(known.Unit, sample.Unit, StringComparison.Ordinal))
        {
            return known.Id;
        }

        HubSql.Execute(
            connection,
            transaction,
            "INSERT INTO metric_series (agent_id, metric_id, value_type, unit) VALUES (@agent, @metric, @type, @unit) "
            + "ON CONFLICT (agent_id, metric_id) DO UPDATE SET value_type = excluded.value_type, unit = excluded.unit;",
            ("@agent", sample.AgentId),
            ("@metric", sample.MetricId),
            ("@type", sample.ValueType),
            ("@unit", sample.Unit));
        long id = ToLong(HubSql.Scalar(
            connection,
            transaction,
            "SELECT id FROM metric_series WHERE agent_id = @agent AND metric_id = @metric;",
            ("@agent", sample.AgentId),
            ("@metric", sample.MetricId)));
        _series[key] = new SeriesRef(id, sample.ValueType, sample.Unit);
        return id;
    }

    private void ForgetAgent(string agentId)
    {
        foreach ((string Agent, string Metric) key in _series.Keys.Where(key => string.Equals(key.Agent, agentId, StringComparison.Ordinal)).ToArray())
        {
            _series.Remove(key);
        }

        _lastText.Clear();
    }

    /// <inheritdoc />
    public Task<int> RemoveAgentAsync(string agentId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);

        return _database.InTransactionAsync(
            (connection, transaction) =>
            {
                int removed = HubSql.Execute(connection, transaction, "DELETE FROM metric_series WHERE agent_id = @agent;", ("@agent", agentId));
                ForgetAgent(agentId);
                return removed;
            },
            cancellationToken);
    }

    // ------------------------------------------------------------------
    // Reading
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public Task<IReadOnlyList<MetricSeriesData>> QueryAsync(MetricQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.MaxPoints, 1);

        return _database.QueryAsync<IReadOnlyList<MetricSeriesData>>(connection => Query(connection, query), cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> CountSeriesAsync(IReadOnlyList<string> agentIds, IReadOnlyList<string> metricIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agentIds);
        ArgumentNullException.ThrowIfNull(metricIds);

        return _database.QueryAsync(
            connection => agentIds
                .Distinct(StringComparer.Ordinal)
                .Sum(agentId => ReadSeries(connection, agentId).Count(series => MetricPattern.MatchesAny(metricIds, series.MetricId))),
            cancellationToken);
    }

    private static List<MetricSeriesData> Query(SqliteConnection connection, MetricQuery query)
    {
        long from = HubSql.ToStored(query.From);
        long to = HubSql.ToStored(query.To);
        var result = new List<MetricSeriesData>();
        foreach (string agentId in query.AgentIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            foreach (SeriesRow series in ReadSeries(connection, agentId))
            {
                if (!MetricPattern.MatchesAny(query.MetricIds, series.MetricId))
                {
                    continue;
                }

                if (MetricValueTypes.IsNumeric(series.ValueType))
                {
                    (List<MetricPoint> points, bool truncated) = query.Resolution == MetricResolution.Raw
                        ? ReadRaw(connection, series.Id, from, to, query.MaxPoints)
                        : ReadRollup(connection, series.Id, query.Resolution, from, to, query.MaxPoints);
                    result.Add(new MetricSeriesData(agentId, series.MetricId, series.ValueType, series.Unit, MetricResolutions.ToText(query.Resolution), points, truncated));
                }
                else
                {
                    (List<MetricPoint> points, bool truncated) = ReadStates(connection, series.Id, from, to, query.MaxPoints);
                    result.Add(new MetricSeriesData(agentId, series.MetricId, series.ValueType, series.Unit, "changes", points, truncated));
                }
            }
        }

        return result;
    }

    private static List<SeriesRow> ReadSeries(SqliteConnection connection, string agentId)
    {
        using SqliteCommand command = HubSql.Command(
            connection,
            null,
            "SELECT id, metric_id, value_type, unit FROM metric_series WHERE agent_id = @agent ORDER BY metric_id;",
            ("@agent", agentId));
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<SeriesRow>();
        while (reader.Read())
        {
            rows.Add(new SeriesRow(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return rows;
    }

    private static (List<MetricPoint> Points, bool Truncated) ReadRaw(SqliteConnection connection, long seriesId, long from, long to, int maxPoints)
    {
        using SqliteCommand command = HubSql.Command(
            connection,
            null,
            "SELECT ts, value FROM metric_raw WHERE series_id = @series AND ts >= @from AND ts <= @to ORDER BY ts LIMIT @limit;",
            ("@series", seriesId),
            ("@from", from),
            ("@to", to),
            ("@limit", (long)maxPoints + 1));
        using SqliteDataReader reader = command.ExecuteReader();
        var points = new List<MetricPoint>();
        while (reader.Read())
        {
            if (points.Count == maxPoints)
            {
                return (points, true);
            }

            points.Add(new MetricPoint(HubSql.FromStored(reader.GetInt64(0)), reader.GetDouble(1), null, null, null, null, null));
        }

        return (points, false);
    }

    private static (List<MetricPoint> Points, bool Truncated) ReadRollup(
        SqliteConnection connection,
        long seriesId,
        MetricResolution resolution,
        long from,
        long to,
        int maxPoints)
    {
        long width = MetricResolutions.BucketMilliseconds(resolution);
        using SqliteCommand command = HubSql.Command(
            connection,
            null,
            resolution == MetricResolution.FiveMinutes ? SelectRollup5m : SelectRollup1h,
            ("@series", seriesId),
            ("@from", MetricResolutions.Floor(from, width)),
            ("@to", to),
            ("@limit", (long)maxPoints + 1));
        using SqliteDataReader reader = command.ExecuteReader();
        var points = new List<MetricPoint>();
        while (reader.Read())
        {
            if (points.Count == maxPoints)
            {
                return (points, true);
            }

            points.Add(new MetricPoint(
                HubSql.FromStored(reader.GetInt64(0)),
                null,
                reader.GetDouble(1),
                reader.GetDouble(2),
                reader.GetDouble(3),
                reader.GetInt64(4),
                null));
        }

        return (points, false);
    }

    private static (List<MetricPoint> Points, bool Truncated) ReadStates(SqliteConnection connection, long seriesId, long from, long to, int maxPoints)
    {
        var points = new List<MetricPoint>();

        // The text that was in effect when the range starts is the first point, so that a text that did not change in the range is still known.
        using (SqliteCommand before = HubSql.Command(
            connection,
            null,
            "SELECT ts, value FROM metric_states WHERE series_id = @series AND ts <= @from ORDER BY ts DESC LIMIT 1;",
            ("@series", seriesId),
            ("@from", from)))
        using (SqliteDataReader reader = before.ExecuteReader())
        {
            if (reader.Read())
            {
                points.Add(new MetricPoint(HubSql.FromStored(reader.GetInt64(0)), null, null, null, null, null, reader.GetString(1)));
            }
        }

        using SqliteCommand command = HubSql.Command(
            connection,
            null,
            "SELECT ts, value FROM metric_states WHERE series_id = @series AND ts > @from AND ts <= @to ORDER BY ts LIMIT @limit;",
            ("@series", seriesId),
            ("@from", from),
            ("@to", to),
            ("@limit", (long)maxPoints + 1));
        using SqliteDataReader rows = command.ExecuteReader();
        while (rows.Read())
        {
            if (points.Count >= maxPoints)
            {
                return (points, true);
            }

            points.Add(new MetricPoint(HubSql.FromStored(rows.GetInt64(0)), null, null, null, null, null, rows.GetString(1)));
        }

        return (points, false);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<LatestMetric>> GetLatestAsync(string agentId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);

        return _database.QueryAsync<IReadOnlyList<LatestMetric>>(
            connection =>
            {
                var latest = new List<LatestMetric>();
                foreach (SeriesRow series in ReadSeries(connection, agentId))
                {
                    bool numeric = MetricValueTypes.IsNumeric(series.ValueType);
                    using SqliteCommand command = HubSql.Command(
                        connection,
                        null,
                        numeric
                            ? "SELECT ts, value FROM metric_raw WHERE series_id = @series ORDER BY ts DESC LIMIT 1;"
                            : "SELECT ts, value FROM metric_states WHERE series_id = @series ORDER BY ts DESC LIMIT 1;",
                        ("@series", series.Id));
                    using SqliteDataReader reader = command.ExecuteReader();
                    if (reader.Read())
                    {
                        DateTimeOffset ts = HubSql.FromStored(reader.GetInt64(0));
                        latest.Add(numeric
                            ? new LatestMetric(agentId, series.MetricId, series.ValueType, series.Unit, ts, reader.GetDouble(1), null)
                            : new LatestMetric(agentId, series.MetricId, series.ValueType, series.Unit, ts, null, reader.GetString(1)));
                    }
                    else
                    {
                        latest.Add(new LatestMetric(agentId, series.MetricId, series.ValueType, series.Unit, null, null, null));
                    }
                }

                return latest;
            },
            cancellationToken);
    }

    // ------------------------------------------------------------------
    // Roll-ups and retention
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public Task<MetricRollupResult> RollUpAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        return _database.InTransactionAsync((connection, transaction) => RollUp(connection, transaction, HubSql.ToStored(now)), cancellationToken);
    }

    private MetricRollupResult RollUp(SqliteConnection connection, SqliteTransaction transaction, long now)
    {
        long closedBefore = now - (long)_options.RollupGrace.TotalMilliseconds;

        // Five minute buckets from the raw samples.
        int buckets5m = 0;
        long? rolled5m = ReadWatermark(connection, transaction, Rollup5mThroughKey);
        long? start5m = rolled5m ?? FirstTime(connection, transaction, "SELECT MIN(ts) FROM metric_raw;", MetricResolutions.FiveMinutesMilliseconds);
        long through5m = MetricResolutions.Floor(closedBefore, MetricResolutions.FiveMinutesMilliseconds);
        if (start5m is { } from5m && from5m < through5m)
        {
            buckets5m = HubSql.Execute(
                connection,
                transaction,
                "INSERT OR REPLACE INTO metric_rollup_5m (series_id, bucket, min_value, avg_value, max_value, sample_count) "
                + "SELECT series_id, (ts / @width) * @width, MIN(value), AVG(value), MAX(value), COUNT(*) FROM metric_raw "
                + "WHERE ts >= @from AND ts < @through GROUP BY series_id, (ts / @width) * @width;",
                ("@width", MetricResolutions.FiveMinutesMilliseconds),
                ("@from", from5m),
                ("@through", through5m));
            WriteWatermark(connection, transaction, Rollup5mThroughKey, through5m);
            rolled5m = through5m;
        }

        // One hour buckets from the five minute buckets, only for hours whose five minute buckets are complete.
        int buckets1h = 0;
        if (rolled5m is { } complete5m)
        {
            long? rolled1h = ReadWatermark(connection, transaction, Rollup1hThroughKey);
            long? start1h = rolled1h ?? FirstTime(connection, transaction, "SELECT MIN(bucket) FROM metric_rollup_5m;", MetricResolutions.OneHourMilliseconds);
            long through1h = Math.Min(
                MetricResolutions.Floor(closedBefore, MetricResolutions.OneHourMilliseconds),
                MetricResolutions.Floor(complete5m, MetricResolutions.OneHourMilliseconds));
            if (start1h is { } from1h && from1h < through1h)
            {
                buckets1h = HubSql.Execute(
                    connection,
                    transaction,
                    "INSERT OR REPLACE INTO metric_rollup_1h (series_id, bucket, min_value, avg_value, max_value, sample_count) "
                    + "SELECT series_id, (bucket / @width) * @width, MIN(min_value), SUM(avg_value * sample_count) / SUM(sample_count), MAX(max_value), SUM(sample_count) "
                    + "FROM metric_rollup_5m WHERE bucket >= @from AND bucket < @through GROUP BY series_id, (bucket / @width) * @width;",
                    ("@width", MetricResolutions.OneHourMilliseconds),
                    ("@from", from1h),
                    ("@through", through1h));
                WriteWatermark(connection, transaction, Rollup1hThroughKey, through1h);
            }
        }

        return new MetricRollupResult(buckets5m, buckets1h);
    }

    private static long? FirstTime(SqliteConnection connection, SqliteTransaction transaction, string sql, long width)
    {
        object? first = HubSql.Scalar(connection, transaction, sql);
        return first is null ? null : MetricResolutions.Floor(ToLong(first), width);
    }

    private static long? ReadWatermark(SqliteConnection connection, SqliteTransaction transaction, string key)
    {
        object? value = HubSql.Scalar(connection, transaction, "SELECT value FROM hub_meta WHERE key = @key;", ("@key", key));
        return value is string text && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) ? parsed : null;
    }

    private static void WriteWatermark(SqliteConnection connection, SqliteTransaction transaction, string key, long value)
    {
        HubSql.Execute(
            connection,
            transaction,
            "INSERT INTO hub_meta (key, value) VALUES (@key, @value) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
            ("@key", key),
            ("@value", value.ToString(CultureInfo.InvariantCulture)));
    }

    /// <inheritdoc />
    public async Task<MetricRetentionResult> ApplyRetentionAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        long nowMilliseconds = HubSql.ToStored(now);
        MetricRetentionResult result = await _database.InTransactionAsync(
            (connection, transaction) =>
            {
                // Raw samples that are not rolled up yet, and five minute buckets that are not folded into an hour yet, stay: nothing is lost when a job lags behind.
                long rawCutoff = Math.Min(
                    nowMilliseconds - (long)_options.RawRetention.TotalMilliseconds,
                    ReadWatermark(connection, transaction, Rollup5mThroughKey) ?? long.MinValue);
                long fiveMinuteCutoff = Math.Min(
                    nowMilliseconds - (long)_options.Rollup5mRetention.TotalMilliseconds,
                    ReadWatermark(connection, transaction, Rollup1hThroughKey) ?? long.MinValue);
                long stateCutoff = nowMilliseconds - (long)_options.StateRetention.TotalMilliseconds;
                long hourCutoff = nowMilliseconds - (long)_options.Rollup1hRetention.TotalMilliseconds;

                long raw = HubSql.Execute(connection, transaction, "DELETE FROM metric_raw WHERE ts < @cutoff;", ("@cutoff", rawCutoff));

                // The newest entry of a series is the text it has now; it stays however old it is.
                long states = HubSql.Execute(
                    connection,
                    transaction,
                    "DELETE FROM metric_states WHERE ts < @cutoff AND EXISTS "
                    + "(SELECT 1 FROM metric_states AS newer WHERE newer.series_id = metric_states.series_id AND newer.ts > metric_states.ts);",
                    ("@cutoff", stateCutoff));
                long rollups5m = HubSql.Execute(connection, transaction, "DELETE FROM metric_rollup_5m WHERE bucket < @cutoff;", ("@cutoff", fiveMinuteCutoff));
                long rollups1h = HubSql.Execute(connection, transaction, "DELETE FROM metric_rollup_1h WHERE bucket < @cutoff;", ("@cutoff", hourCutoff));
                return new MetricRetentionResult(raw, states, rollups5m, rollups1h);
            },
            cancellationToken).ConfigureAwait(false);

        if (result.Total > 0)
        {
            // Move the write-ahead log into the database file and shrink it again; the pages that were freed are reused by the next samples.
            await _database.ExecuteAsync(connection => HubSql.Execute(connection, null, "PRAGMA wal_checkpoint(TRUNCATE);"), cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    // ------------------------------------------------------------------
    // Statistics
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public Task<MetricStoreStats> GetStatsAsync(CancellationToken cancellationToken)
    {
        return _database.QueryAsync(
            connection =>
            {
                long pageSize = ToLong(HubSql.Scalar(connection, null, "PRAGMA page_size;"));
                return new MetricStoreStats(
                    Count(connection, "SELECT COUNT(*) FROM metric_series;"),
                    Count(connection, "SELECT COUNT(*) FROM metric_raw;"),
                    Count(connection, "SELECT COUNT(*) FROM metric_states;"),
                    Count(connection, "SELECT COUNT(*) FROM metric_rollup_5m;"),
                    Count(connection, "SELECT COUNT(*) FROM metric_rollup_1h;"),
                    ToLong(HubSql.Scalar(connection, null, "PRAGMA page_count;")) * pageSize,
                    ToLong(HubSql.Scalar(connection, null, "PRAGMA freelist_count;")) * pageSize);
            },
            cancellationToken);
    }

    private static long Count(SqliteConnection connection, string sql) => ToLong(HubSql.Scalar(connection, null, sql));

    private static long ToLong(object? value) => Convert.ToInt64(value, CultureInfo.InvariantCulture);
}
