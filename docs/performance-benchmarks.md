# Performance benchmarks

`KittyClaw.Benchmarks` is a standalone .NET 10 console runner. It measures elapsed time
and managed allocations for evidence status computation, decision brief composition,
evidence JSON serialization, and Markdown rendering, with 100 and 1,000 items each.
It also measures SQLite reads/writes and the main API read endpoints. The suite has
38 scenarios in total. Fixtures are deterministic and created outside measured regions.
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

Two simple projects contain 100 and 1,000 synthetic tickets, each with one comment, one activity
and a shared label. Setup creates valid pipeline/column identities using real services.
Every workload is exercised successfully before measurements. Startup, seeding and
migrations are excluded: this measures warm steady-state access. Two additional projects
contain complex boards described below; registry scenarios therefore list four projects.

| Scenario | Measured operation |
| --- | --- |
| `sqlite.projects/4` | Registry listing through `ProjectService` |
| `sqlite.board/{size}` | Ticket summary query through `TicketService`, including relationships |
| `sqlite.detail/{size}` | Ticket detail with comments, activities and labels |
| `sqlite.update/{size}` | Fresh context, tracked read and committed `SaveChangesAsync` |
| `api.projects/4` | `GET /api/projects` |
| `api.project/{size}` | `GET /api/projects/{slug}` |
| `api.board/{size}` | `GET /api/projects/{slug}/tickets` |
| `api.search/{size}` | Ticket list filtered by search text and status |
| `api.detail/{size}` | `GET /api/projects/{slug}/tickets/{id}` |
| `api.columns/{size}` | `GET /api/projects/{slug}/columns` |
| `sqlite.concurrent/{size}/2r-1w` | Concurrent batch: two readers, one writer |
| `sqlite.concurrent/{size}/4r-2w` | Concurrent batch: four readers, two writers |
| `sqlite.complex-board/{size}` | Relational board through `TicketService` |
| `sqlite.complex-detail/{size}` | Parent ticket with children and dependencies |
| `api.complex-board/{size}` | Board endpoint with relationship validation |
| `api.complex-detail/{size}` | Detail endpoint with relationship validation |

Complex boards contain 100 or 1,000 tickets in groups of one parent and four children.
Every ticket has five comments, three activities and three labels drawn from twelve
shared labels. Parents form an acyclic chain of unresolved dependencies (19 or 199
edges). Workloads verify child and blocker relationships, not only successful status codes.

Concurrency batches use the simple boards, independent EF contexts/connections, and
worker threads released through a shared start gate. Each worker performs four operations:
readers load the full board, writers commit atomic token-counter increments on separate
tickets in the same database. Writers contend on SQLite's database-level write lock.
Every batch verifies all increments persisted, and read counts stayed intact. No extra
application retry is added; lock timeouts or other exceptions fail the run. Write commands
have a five-second timeout; readers retain the application's normal timeout. All workers
finish before cleanup or the next batch. Row counts and relationship history stay bounded.

For concurrent scenarios, **ns/op and B/op describe the entire batch**, including worker
coordination and final integrity verification: 12 operations for 2r-1w, 24 for 4r-2w.
They are not individual request latency or p95 statistics. Allocation counts include all
worker threads. Scheduling and lock waits increase variability, especially on shared CI.

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
The suite is now `core-io-v3`; older reports require regeneration.

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
