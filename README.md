# Request Routing

[![CI](https://github.com/AryaZed/request-routing/actions/workflows/ci.yml/badge.svg)](https://github.com/AryaZed/request-routing/actions/workflows/ci.yml)

Assigns incoming requests to the right expert automatically, and stays correct under concurrent traffic and with several instances of the app on one database.

Stack: .NET 10, ASP.NET Core Minimal APIs, EF Core 10, SQL Server.
Every design decision, with the alternatives considered, is in [docs/DECISIONS.md](docs/DECISIONS.md).

Persian (فارسی): [README.fa.md](README.fa.md), [docs/DECISIONS.fa.md](docs/DECISIONS.fa.md).

## Quick start

You need the .NET 10 SDK and either a local SQL Server or Docker.

**Run against a local SQL Server.** The Development settings use `Server=.` with Windows authentication, and the database is created on first start.

```bash
dotnet run --project src/RequestRouting.Api
```

API reference: `/scalar` (Development only). Example calls: [api.http](api.http).

**Run two instances with Docker:** ports 5001 and 5002 share one SQL Server. A one-shot `migrate` service prepares the database first. A one-shot `migrate` service prepares the database first.

```bash
docker compose up --build
```

**Run the tests.** They use real SQL Server, because they test real locking. Point them at a server, or leave the variable unset to use a Testcontainers container (needs Docker). Each test creates and drops its own `RequestRoutingTest_<guid>` database.

```bash
ROUTING_TEST_SQLSERVER="Server=.;Trusted_Connection=True;TrustServerCertificate=True" dotnet test --solution RequestRouting.slnx
```

**See requirement 2 with two real processes** (Windows PowerShell, local SQL Server). Sends 20 concurrent requests and 20 retries for an expert with capacity 5, then drops its database.

```bash
powershell -ExecutionPolicy Bypass -File scripts/two-instances-demo.ps1
```

## API

| Method | Route | Purpose |
|---|---|---|
| `POST` | `/api/requests` | Submit a request. `201` if new, `200` if `requestKey` already exists, `409` if that key was used for a different request |
| `GET` | `/api/requests/{id}` | Read a request |
| `POST` | `/api/requests/{id}/close` | Close a request and assign the next one. Safe to repeat |
| `GET` | `/api/requests/pending?take=50` | The waiting queue, in the order it will be served |
| `POST` | `/api/experts` | Create an expert |
| `GET` | `/api/experts/{id}` | Read an expert |
| `PATCH` | `/api/experts/{id}` | Change capacity, active state or topics. May assign waiting requests |
| `GET` | `/health/live`, `/health/ready` | Liveness, and readiness including the database |

Errors use RFC 9457 Problem Details. Business-rule conflicts carry a stable code in `type`, for example `urn:request-routing:request-key-reused`.

## The rules

### Choosing an expert

Among active experts with the request's topic and free capacity:

1. **Lowest open/capacity ratio.** The task's example: 2 of 4 is 0.5 and 3 of 10 is 0.3, so the second expert is chosen.
2. **On a tie, the expert whose last assignment is oldest**, with never-assigned experts first.
3. **On a further tie, the lowest id.**

**Why this tie rule.** Ties are common: every idle expert is tied at 0. A fixed order alone would always favour the same person, so low ids would get work first every time. Longest-idle spreads work fairly over time, like round-robin, with no extra state because it reuses `LastAssignedAt`. The id exists only to make the order total, so every instance reaches the same decision and tests are repeatable. Random selection was rejected because it can't be reproduced.

If no expert qualifies, the request stays `Pending`.

### Queue order

Every request gets a **rank** when it is created, and the queue is served in ascending rank:

```text
Normal: rank = created time
Urgent: rank = created time - 30 minutes   (Queue:UrgentHeadStart)
```

An urgent request is treated as if it had arrived 30 minutes earlier. **Example**, with one expert who is busy:

| Request | Created | Rank |
|---|---|---|
| N1 normal | 10:00 | 10:00 |
| U1 urgent | 10:05 | 09:35 |
| U2 urgent | 10:20 | 09:50 |
| U3 urgent | 10:31 | 10:01 |

Service order: **U1, U2, N1, U3**. U1 and U2 overtake N1, as urgent should. U3 arrives when N1 has already waited longer than the head start, so N1 goes first.

- **Urgent beats normal:** any urgent request overtakes normal requests less than 30 minutes older than it.
- **Normal can't starve:** a normal request created at time T can only be overtaken by urgent requests created before T + 30 minutes. That set is finite, so however long the stream of urgent requests continues, the normal request's turn comes.
- **Nothing blocks the queue:** only topics with an available expert are considered, so a request nobody can serve is simply passed over. It is assigned as soon as an expert for it becomes available.

Tuning is one number: `0` is plain first-come-first-served, and a larger value favours urgent more strongly.

### Staying correct under concurrency

| Guarantee | How | Backstop in the database |
|---|---|---|
| Capacity is never exceeded | One assignment at a time under `sp_getapplock`, with `UPDLOCK` on the experts because closing doesn't take that lock | `CHECK (OpenCount <= Capacity)` |
| A request has one expert | The request row is locked while `Pending`, and has a single `ExpertId` column | `CHECK` tying `Status` to `ExpertId` |
| Same `RequestKey`, one request | Insert, and treat a duplicate as a replay | Unique index on `RequestKey` |
| Works with several instances | All coordination is in the database, with nothing in process memory | |

Every request is inserted as `Pending`, and one `Dispatcher` is the only code that assigns. Creating a request, closing one and changing an expert all **commit first, then dispatch**. Whichever change commits last therefore sees all the others, so no wakeup is lost. If a process dies between commit and dispatch, a sweeper on every instance recovers the request within 30 seconds.

### Measured behaviour

Two instances and SQL Server in Docker Desktop on a developer laptop, so read these as relative. Each run was checked afterwards for over-capacity experts, wrong counters and duplicate keys: there were none, and no deadlocks.

| Scenario | Throughput | p50 | p95 | Errors |
|---|---|---|---|---|
| 4 concurrent clients | 93 req/s | 30 ms | 73 ms | 0 |
| 64 concurrent, overlapping topics | 101 req/s | 401 ms | 1.2 s | 0 |
| 64 concurrent, with 100,000 blocked requests waiting | 117 req/s | 358 ms | 1.2 s | 0 |
| 256 concurrent, burst | 87 req/s | 2.2 s | 6.8 s | 0 |

Load testing changed the design: row locks alone looked more scalable on paper, and measured up to 3 times slower. See [the comparison](docs/DECISIONS.md#concurrency-the-central-decision).

## Layout

```text
src/
  RequestRouting.Domain          Entities and rules: ExpertSelector, QueuePolicy. No dependencies
  RequestRouting.Application     Use cases, Dispatcher, and the IAssignmentStore port
  RequestRouting.Infrastructure  EF Core, the locking SQL, migrations, the sweeper
  RequestRouting.Api             Endpoints, validation, error mapping
tests/
  RequestRouting.UnitTests         Pure rules
  RequestRouting.IntegrationTests  Real HTTP, real SQL Server, two app instances
```

## Operations

- **Snapshot isolation.** Databases created by EF Core get `READ_COMMITTED_SNAPSHOT ON` automatically. For a database created another way, run this in a maintenance window, because it rolls back open transactions. Correctness doesn't depend on it, and the concurrency tests run both ways, but it reduces blocking between readers and writers.

  ```sql
  ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
  ```

- **Migrations.** With several instances, run `--migrate-only` once as a deployment step before they start, as Docker Compose does. `Database:MigrateOnStartup` is on in Development settings for a single instance only: instances racing to create a database can crash.
