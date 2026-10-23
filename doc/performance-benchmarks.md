# Performance benchmarks

`KittyClaw.Benchmarks` is a standalone .NET 10 console runner. It measures elapsed time
and managed allocations for evidence status computation, decision brief composition,
evidence JSON serialization, and Markdown rendering, with 100 and 1,000 items each.
It also measures SQLite reads/writes and the main API read endpoints. The suite has
26 scenarios in total. Fixtures are deterministic and created outside measured regions.
No LLM, external network, user data, or live instance is used.

## SQLite and API isolation

Each run starts a disposable ASP.NET Core `TestServer` through `WebApplicationFactory`,
with a unique `kittyclaw-perf-*` temporary data directory. The host uses the real
application DI setup, services, handlers, EF Core contexts and SQLite files. All
application `IHostedService` registrations are removed before startup, disabling
automation, dispatch, git watchers, telemetry, updates and recovery. Development mode
and the desktop-off setting are explicit. No TCP listener is opened; the client talks
to the in-memory server and never uses ports 5230, 5231 or 5232. The factory reads its
own live `/openapi/v1.json` to verify benchmark routes.

Two projects contain 100 and 1,000 synthetic tickets, each with one comment, one activity
and a shared label. Setup creates valid pipeline/column identities using real services.
Every workload is exercised successfully before measurements. Startup, seeding and
migrations are excluded: this measures warm steady-state access, without contention.

| Scenario | Measured operation |
| --- | --- |
| `sqlite.projects/2` | Registry listing through `ProjectService` |
| `sqlite.board/{size}` | Ticket summary query through `TicketService`, including relationships |
| `sqlite.detail/{size}` | Ticket detail with comments, activities and labels |
| `sqlite.update/{size}` | Fresh context, tracked read and committed `SaveChangesAsync` |
| `api.projects/2` | `GET /api/projects` |
| `api.project/{size}` | `GET /api/projects/{slug}` |
| `api.board/{size}` | `GET /api/projects/{slug}/tickets` |
| `api.search/{size}` | Ticket list filtered by search text and status |
| `api.detail/{size}` | `GET /api/projects/{slug}/tickets/{id}` |
| `api.columns/{size}` | `GET /api/projects/{slug}/columns` |

Writes toggle a fixed-size description, keeping row counts and activity history bounded.
API measurements include routing, service/database work, server JSON serialization,
body consumption and client JSON parsing; they exclude real socket/network latency.
Responses must be HTTP 200 and valid JSON. All responses and contexts are disposed.
The host shuts down, only its SQLite pools are cleared, the temporary directory is
deleted, and previous environment values are restored even on failure. Do not start
another application host in the benchmark process while these scoped overrides are active.
UI rendering, API writes, cold startup and provider response times are outside this suite.

## Local reference and comparison

Run from the repository root in a quiet environment, using the same machine, SDK,
runtime and power settings for both revisions:

```powershell
$env:DOTNET_TieredCompilation = '0'
dotnet run --project KittyClaw.Benchmarks -c Release -- run .artifacts/performance/baseline.json
# Apply the change being assessed, then measure again.
dotnet run --project KittyClaw.Benchmarks -c Release -- run .artifacts/performance/current.json
dotnet run --project KittyClaw.Benchmarks -c Release --no-build -- compare .artifacts/performance/baseline.json .artifacts/performance/current.json 25
```

Each workload warms up for 500 ms, calibrates a batch to at least 100 ms, and
collects nine batches. Reports retain sorted time samples, median nanoseconds per
operation, median allocated bytes per operation, iteration counts and environment
metadata. Synchronous core allocations use the current thread. Async SQLite/API
workloads use process-wide allocation deltas so thread switches do not lose allocations.
`AllocationScope` records this distinction and the comparator rejects scope mismatches.
Process-wide measurements include incidental framework allocations and can be noisier.
The runner holds results alive to prevent dead-code elimination. Release is required.

Comparison fails when either median time or allocations increases by **more than
25%** (configurable through the last argument). Exit codes: `0` pass, `1` regression,
`2` invalid arguments/reports or incompatible environment/scenario sets. A zero-byte
baseline flags any new allocation. Missing scenarios and duplicate/invalid metrics
are errors rather than silent passes. Change the suite identifier when fixtures or
measurement methodology change, and regenerate both reports.
The suite is now `core-io-v2`; old `core-v1` reports require regeneration.

## CI

The performance workflow builds the PR base and candidate, then runs the same harness
from the candidate against both versions of Core and Web on a single Ubuntu runner. Both
builds finish before measurement. It compares reports at 25% and uploads both JSON
files even when comparison fails. Manual dispatch compares the selected revision
against itself as a smoke check. No machine-specific reference is committed.

Shared CI runners can be noisy. Inspect samples and repeat a failed run before
attributing a small time regression to code. Allocation changes are usually more
stable. For tighter thresholds use a dedicated runner; the environment metadata
cannot detect CPU model, thermal state, power mode or competing processes. This
workflow does not establish statistical significance or cover application-wide
performance. Add representative workloads as bottlenecks are identified.
