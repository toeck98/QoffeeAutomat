# Sizing the hub for metric history

The hub keeps the metrics of the PCs that are enrolled with it, so that you can compare PCs and look at the past in the hub
interface. This page tells you how much disk, CPU and memory that takes, how to change what is kept, and how to calculate the
size for your fleet before you enroll it.

!!! note "Estimates and measurements"
    The row sizes below are **estimates** from the layout of the SQLite rows and indexes, with pages about 70 % full. Your
    figures will differ a little. The load test of the hub (see [Measure it yourself](#measure-it-yourself)) writes the real
    figures of your build to its output; use them in the formula when you plan close to a limit.

## What is stored

For every metric of every enrolled PC the hub keeps one *series*. What goes into a series depends on the type of the metric:

| Metric type | Stored as | Default retention |
|-------------|-----------|-------------------|
| number, boolean | **raw samples**, one per collection interval | 48 hours |
| number, boolean | **5 minute roll-ups**: minimum, average, maximum and count per bucket | 30 days |
| number, boolean | **1 hour roll-ups**: the same per hour | 2 years |
| string, enum, timestamp | a **change log**: a row only when the text changes | 90 days (the newest entry is always kept) |

A boolean is stored as 0 or 1, so its average in a roll-up is the share of the time it was true. The roll-ups are computed by
the hub from the raw samples; the hour is computed from the 5 minute buckets, so the raw samples may expire long before the
hour is rolled up and nothing is lost. A raw sample that has not been rolled up yet is never deleted, so a stopped or failed
job does not lose data.

When you remove a PC from the hub, **all of its series are removed with it**.

## What is collected

By default the hub collects **every metric** of every PC **every 60 seconds**. It does this by subscribing to the metrics at
that interval; a PC sends a value at most once per interval, so the load on the PC and on the network is small. If another part
of the hub wants a metric more often (for example an open dashboard), the PC sends more values, but the hub still keeps about
one sample per interval.

You change the policy with environment variables of the hub (container, service or `docker run -e`):

| Variable | Meaning | Default |
|----------|---------|---------|
| `QOFFEE_HUB_METRICS_COLLECT` | Metric ids or prefixes to collect, separated by commas. `cpu.*` is every metric whose id starts with `cpu.`; `*` alone is every metric. | `*` |
| `QOFFEE_HUB_METRICS_INTERVAL` | Collection interval in seconds, 1 to 86 400. | `60` |
| `QOFFEE_HUB_RETENTION_RAW_HOURS` | How long raw samples are kept, in hours (at least 1). | `48` |
| `QOFFEE_HUB_RETENTION_5M_DAYS` | How long 5 minute roll-ups are kept, in days (fractions are allowed; at least 1 hour). | `30` |
| `QOFFEE_HUB_RETENTION_1H_DAYS` | How long 1 hour roll-ups are kept, in days (at least 1). | `730` |
| `QOFFEE_HUB_RETENTION_STATES_DAYS` | How long the change log of text metrics is kept, in days (at least 1). | `90` |

A value that is not a positive number, or a retention below the minimum, stops the hub at start with a message that names the
variable. The policy is the same for every PC; a metric that a PC does not have is simply not stored.

The retention job runs once an hour. The database file does **not shrink** when rows are deleted: SQLite keeps the free pages
and reuses them for new samples, so the file grows to the size of the full retention windows and then stays there.

## The formula

Let

* *N* be the number of PCs,
* *M* the number of **number and boolean** metrics collected per PC (text metrics are counted separately),
* *i* the collection interval in seconds,
* *R*<sub>raw</sub>, *R*<sub>5m</sub>, *R*<sub>1h</sub> the retentions in seconds.

The number of rows in a full database is, per series,

| Table | Rows per series |
|-------|-----------------|
| raw | *R*<sub>raw</sub> / *i* |
| 5 minute roll-up | *R*<sub>5m</sub> / 300 |
| 1 hour roll-up | *R*<sub>1h</sub> / 3600 |

and the size is about

```text
size = N × M × ( Rraw / i × 55  +  R5m / 300 × 85  +  R1h / 3600 × 85 )   bytes
     + N × T × C × 80                                                      (text metrics)
```

where *T* is the number of text metrics per PC and *C* the number of changes of such a metric within its retention (a metric
that changes twice a day for 90 days has *C* = 180). The constants are the estimated bytes per row **including the index**
that each table has for the clean-up: 55 for a raw sample, 85 for a roll-up bucket, 80 for a change.

The roll-ups do not depend on the interval: a series always has 288 buckets of 5 minutes a day and 24 buckets of one hour. A
longer interval therefore only makes the raw part smaller, and the 5 minute and 1 hour parts are what you save by **shortening
their retention**.

### Example: 50 PCs with 30 metrics

27 of the 30 metrics are numbers or booleans, 3 are text that change rarely (they add only a few hundred kilobytes). 1 350
series, 60 second interval, default retention:

| Table | Per series | For 1 350 series |
|-------|-----------:|-----------------:|
| raw, 48 hours = 2 880 rows | 158 kB | 214 MB |
| 5 minute, 30 days = 8 640 rows | 734 kB | 991 MB |
| 1 hour, 2 years = 17 520 rows | 1 489 kB | 2 010 MB |
| **Total** | **2.38 MB** | **3.2 GB** |

The database grows while the windows fill up, and stops growing when they are full:

| Age of the hub | Size (about) | Growth per day while the table fills |
|----------------|-------------:|--------------------------------------|
| 2 days | 285 MB | raw: 107 MB (until 2 days), then 0 |
| 30 days | 1.3 GB | 5 minute: 33 MB (until 30 days), then 0 |
| 1 year | 2.2 GB | 1 hour: 2.8 MB (until 2 years), then 0 |
| 2 years and later | 3.2 GB | 0 |

### Other fleet sizes

All with 27 numbers and booleans per PC at 60 seconds. "Default" is the retention above; "short" is 24 hours raw, 7 days of
5 minutes and 90 days of hours, which is enough for "what happened last week" and costs 0.43 MB per series.

| PCs | Series | Default retention | Short retention |
|----:|-------:|------------------:|----------------:|
| 5 | 135 | 0.32 GB | 59 MB |
| 10 | 270 | 0.64 GB | 117 MB |
| 50 | 1 350 | 3.2 GB | 0.59 GB |
| 200 | 5 400 | 12.9 GB | 2.3 GB |

With the short retention the variables are

```text
QOFFEE_HUB_RETENTION_RAW_HOURS=24
QOFFEE_HUB_RETENTION_5M_DAYS=7
QOFFEE_HUB_RETENTION_1H_DAYS=90
```

!!! tip "What saves the most"
    1. **Collect fewer metrics.** Size is proportional to *M*. `QOFFEE_HUB_METRICS_COLLECT=cpu.*,memory.*,disk.*,network.*`
       is usually all that a dashboard needs.
    2. **Shorten the hour roll-ups.** They are the largest part by default, because they are kept for two years.
    3. **Shorten the 5 minute roll-ups** if you rarely look at ranges of one to four weeks.
    4. A longer interval (say 120 seconds) halves only the raw part, which is the smallest.

The history is part of `hub.db`, so it is also part of every copy that you make of the hub's data directory. Keep that in mind
for the size of your backups, and see [Backup and restore](backup-restore.md) for what else to save.

## CPU

Collecting is cheap: 50 PCs with 30 metrics at 60 seconds are 1 500 values per minute, or 25 per second. The hub keeps the
values in memory, writes them in one transaction every 5 seconds (or when 2 000 are waiting), rolls up the buckets that ended
once a minute and applies the retention once an hour.

The load test of the hub simulates exactly this load for two hours, with the same intervals of the jobs, and **fails if the
hub needs 5 % or more of one core** for the simulated time. It also checks that nothing was dropped and that the roll-ups kept
up. The share it measured on your machine is printed in the output of the test.

Writing is limited by the disk, not by the CPU: the hub uses SQLite in write-ahead-log mode with `synchronous=NORMAL`, so a
transaction does not wait for the disk to flush. A second load test pushes 150 000 samples from 50 threads while the writer is
running and requires more than 5 000 samples per second, which is 200 times what the fleet above produces.

If the database cannot keep up (a very slow disk), the hub **drops new samples** instead of growing without limit and logs
`N metric sample(s) were dropped because the database could not keep up`. The queue holds up to 200 000 samples.

## Memory

| Part | Memory |
|------|--------|
| Waiting samples | normally a few hundred (5 seconds of the fleet); at most 200 000, roughly 15 to 20 MB, when the disk stalls |
| Bookkeeping per PC | a few kilobytes (the time of the last sample of each metric and the manifest) |
| Database cache | SQLite default, about 2 MB |
| A query | about 100 bytes per point while it is read, and the answer is JSON of about 60 bytes per point |

A single query is limited to 50 PCs, 100 metric ids or prefixes, 200 series and 5 000 points per series, so one request can
use at most roughly 160 MB (reading plus the JSON answer) for a short time. A dashboard that compares one metric of 50 PCs over
a week (50 series of 2 016 points of the 5 minute roll-up) needs about 15 MB.

## How the history is read

The query API chooses the resolution from the length of the range, so a dashboard does not have to know the retention:

| Range | Resolution | Points at the default interval |
|-------|------------|-------------------------------:|
| up to 6 hours | raw samples | 360 |
| up to 7 days | 5 minute roll-up | 2 016 |
| longer | 1 hour roll-up | 17 520 for two years |

When the start of the range is older than the retention of the chosen resolution, the next coarser one is used, because that is
the one that still has the data. A request can name the resolution (`raw`, `5m` or `1h`) instead. Text metrics are always
returned as the change log, and a range that starts after the last change still returns the text that was in effect.

## Measure it yourself

The tests of the metric collection are in `tests/QoffeeAutomat.Hub.Tests/Metrics/`. To see the measured figures of your build,
run the load test and show its output:

```bash
dotnet test tests/QoffeeAutomat.Hub.Tests --filter "Category=Metrics&FullyQualifiedName~MetricLoadTests" --logger "console;verbosity=detailed"
```

It prints

* the CPU time, the wall time and the share of one core for the simulated two hours,
* the growth of the database in bytes, per row and per stored sample (use these instead of the 55, 85 and 80 above),
* the throughput of the writer in samples per second.

You can also look at your own hub: the size of `hub.db` (and `hub.db-wal`) in the data directory, after the 5 minute tier has
had 30 days to fill, is the number to compare with the table above.

!!! note "A simulated 30 days"
    A separate test runs 30 simulated days of six series with very short retentions (1 hour raw, 2 hours of 5 minutes, one day of
    hours and of text changes) and checks that every table stays inside its window and that the file does not keep growing after
    the third day. That is the proof that the retention works; the sizes in this page are what the windows cost.
