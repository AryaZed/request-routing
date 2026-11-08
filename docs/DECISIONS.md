# Design decisions

Each decision states what was chosen, what was rejected and why. Sources are Microsoft's documentation unless noted. Where behaviour was measured instead of taken from the docs, that is stated. Persian version: [DECISIONS.fa.md](DECISIONS.fa.md).

## Architecture

| Decision | Choice | Rejected, and why |
|---|---|---|
| Structure | Four projects with compiler-enforced dependencies: Domain, Application, Infrastructure, Api | A single project is viable at this size, but nothing would stop domain rules depending on SQL. Microservices have no justification here |
| Mediator | None. Endpoints call services directly | MediatR adds indirection and a dependency for no gain with this few use cases |
| Repository | One port, `IAssignmentStore`, named after what the system does | A generic repository can't express "assign the next request under the dispatch lock", the operation that matters |
| Mapping | Small `From` methods | AutoMapper hides field changes from the compiler |
| API style | Minimal APIs with `TypedResults` | Controllers offer nothing extra here, and typed results feed OpenAPI directly |
| Validation | .NET 10 built-in `AddValidation` with data annotations | FluentValidation is another dependency for a few simple rules |
| Errors | One `DomainRuleException` with a stable code, mapped to Problem Details | A `Result<T>` library adds ceremony to every call for a handful of rule failures |
| Time | `TimeProvider`, faked in tests | `DateTime.UtcNow` makes the aging policy untestable without sleeping |

## Database

**SQL Server** was chosen because it is the usual pairing in .NET shops, and because the task's hardest requirement could be tested on it for real here. Everything specific to it is in `SqlAssignmentStore` and the migration. PostgreSQL would serve equally well, using `pg_advisory_xact_lock` and `FOR UPDATE`.

- **`int`/`bigint` identity keys.** Compact, and clustered inserts stay sequential. `uniqueidentifier` clustered keys fragment and sort in an unintuitive order.
- **`OpenCount` is a column, not `COUNT(*)`.** Computing it can never drift, but then the database can't enforce capacity. A column allows `CHECK (OpenCount <= Capacity)`, which holds even if the application has a bug. It is only written in the same transaction as the request's status, and a test compares it with the real count.
- **Filtered indexes on pending rows.** The queue indexes stay small however much closed history builds up.
- **Topics are validated codes** (`finance`), not free text, so matching doesn't depend on collation or on Persian and Arabic variants of a letter such as `ی`/`ي`. Display names belong in a separate column if they are ever needed.
- **`RequestKey` uses a binary collation**, because it is an opaque client value. `abc` and `ABC` are different keys.

## Concurrency: the central decision

The chosen approach is one short application lock around each assignment, plus row locks on the experts involved:

```text
1. Peek without locks: is anything servable? If not, stop, without ever queuing for the lock
2. In a transaction, take the dispatch lock: sp_getapplock, released at commit
3. Find the topic of the earliest servable request
4. Lock that topic's experts: UPDLOCK, using a pinned plan
5. Re-read their topics, then choose the best expert in C# (ExpertSelector)
6. Lock the head of that topic's queue, assign, and commit
```

**This reverses my first design, because of measurement.** I originally rejected a single lock as a bottleneck and used row locks on the experts alone, expecting unrelated topics to run in parallel. Load testing proved that wrong:

| Two instances in Docker, 64 concurrent clients | Row locks only | Dispatch lock |
|---|---|---|
| Overlapping topics | 37 req/s, p95 11.6s | **120 req/s, p95 1.2s** |
| Disjoint topics, the case row locks should win | 62 req/s, p95 6.9s | **115 req/s, p95 1.2s** |
| Burst of 256 concurrent clients | 63 req/s, p99 20.3s | **113 req/s, p99 7.4s** |
| One contended expert | 143 req/s, p95 954ms | 112 req/s, p95 1.1s |

The cause was a lock convoy: 69,633 `LCK_M_U` waits for about 6,000 requests, and zero deadlocks. Every caller worked on the globally earliest request, so they all converged on the same experts, woke up, found the work already done, and queued again. Only the one-expert case, where a single lock was already what happened, was faster, by about 20%. The dispatch lock also made the code simpler, and queue order strict across all topics.

These numbers come from a developer laptop running Docker Desktop, so read them as relative. Repeated runs of the final code ranged from 87 to 117 req/s. The limit isn't CPU: one assignment at a time means one log flush at a time.

| Alternative | Why not |
|---|---|
| In-process `lock`/`SemaphoreSlim` | Fails the task's explicit two-instance requirement |
| Distributed lock (Redis) | New infrastructure to run, and a new failure mode, when the database is already the coordination point |
| Row locks only | Measured above. The original design |
| Conditional `UPDATE ... WHERE OpenCount < Capacity` only | Protects capacity well, but two dispatchers read the same "least loaded" expert, so load balancing is wrong under concurrency. A test requires an exact 5/5 split |
| Optimistic `rowversion` with retries | Under exactly the contention the task describes, most attempts fail and retry |
| `SERIALIZABLE` | Range locks cause more blocking and deadlocks. Microsoft's deadlock guidance advises against higher isolation levels when they aren't required |
| `READPAST` (skip locked rows) | Good for parallel throughput, but one dispatcher can take a later request while an earlier one is locked. Priority order matters more here |
| Stored procedure | Moves the selection rules into SQL, away from the unit tests |
| Message queue, single consumer | Solves ordering, but the client no longer gets its assignment in the response, and a broker plus an outbox is too much for this requirement |

**Why row locks remain.** Closing a request and editing an expert don't take the dispatch lock, so that closing never queues behind assignment. They change the same expert rows, so those rows still need locking.

**Why the expert query's plan is pinned.** Left alone on small tables, the optimizer chose a clustered index scan, which takes `UPDLOCK` on every expert in turn and blocks on any of them. `FORCE ORDER`, `INNER LOOP JOIN` and `FORCESEEK` read the topic index first, so only that topic's experts are locked, and always in id order.

**Deadlocks.** Dispatching takes the dispatch lock, then experts in id order, then a request. Closing takes its request and then its expert, but a request being closed with an expert is `Assigned`, and dispatchers only touch `Pending` requests. The load tests recorded zero deadlocks. The backstop is `EnableRetryOnFailure`, with each whole transaction run inside the execution strategy on a new `DbContext`, as Microsoft's connection-resiliency guidance requires. The new context matters: EF doesn't overwrite tracked entities, so reusing one would hide the fresh values the lock just read.

**Why the topics are re-read after locking.** Measured on SQL Server 2025 with snapshot isolation on: a locking query that reads `ExpertTopics` first and then waits for the expert's lock returned a topic that had been removed during the wait. The second statement sees the current topics. A test covers this, and removing the re-check makes that test fail.

**Why topic parameters are typed `varchar`.** Raw SQL parameters default to `nvarchar`. Against the `varchar` column, the conversion turned an index seek into a range scan through every waiting request. With 100,000 blocked requests, the head-of-queue query cost 605 reads and throughput fell to 18 req/s. Typed correctly, it costs 8 reads and throughput is 117 req/s, the same as with no backlog.

**Reads for the response** ignore client cancellation once the change has committed. A client disconnecting, which is the task's own scenario, must not abandon the queue.

## Queue policy

**Chosen: a fixed head start applied once, when the request is created.** An urgent request is ranked as if it arrived 30 minutes earlier. This is Earliest Deadline First with deadlines that differ by a constant.

| Alternative | Why not |
|---|---|
| Strict priority | Starves normal requests, which the task forbids |
| Promote to urgent after N minutes | A background job must rewrite rows, and promoted requests then compete by arrival time with real urgent ones |
| Weighted ratio, such as 3 urgent per 1 normal | Needs a shared counter across instances, and "normal requests still wait behind 3" gives no time bound |
| Computed aging score at query time | Can't use an index, so every dequeue sorts the whole queue |

The stored rank means no background job, no shared state and an index that directly gives the order. The cost is that changing the head start only affects new requests, which is also what keeps existing requests' positions stable.

## Reliability

**Commit, then dispatch, with a sweeper.** Assigning inside the create transaction would be atomic, but a new request could then take capacity ahead of older waiting ones. A failed dispatch must not fail a request that has already been committed either, so the failure is logged and retried by the sweeper. In a chaos run, killing one instance during traffic left no servable request waiting and no invariant broken.

**The sweeper has no leader election**, because `Dispatcher` is already safe to run concurrently. Each request's dispatch is capped at 50 assignments, so raising one expert's capacity can't make one HTTP call do unbounded work. The sweeper finishes anything left over.

**Migrations run as a separate one-shot step**, the `migrate` service in Docker Compose and `--migrate-only` elsewhere. Running real containers found why this matters. Letting each instance migrate at startup on an empty server hit two races, because EF Core's migration lock lives inside the database and so can't cover creating it: instances collided on `CREATE DATABASE`, and, intermittently, EF's `ALTER DATABASE ... READ_COMMITTED_SNAPSHOT` blocked for 60 seconds against the other instance's connection and killed that instance. A first attempt with a retry only hid the problem. With the separate step, four out of four cold starts were clean. `MigrateOnStartup` remains for a single instance in development.

**Isolation level isn't set by a migration.** An earlier version did this with `ROLLBACK IMMEDIATE`, which would roll back live transactions on other instances during a rolling deployment. EF already enables it on databases it creates. Otherwise it is a documented operator step, and the tests run both ways.

## Testing

- **Real SQL Server, real HTTP, two hosted instances, and the sweeper off** unless a test is about it. In-memory providers have no locks, so they can't test the concurrency requirement.
- **Tests fail on logged dispatch failures.** Those failures are deliberately not returned to clients, so without this check the `CHECK` constraint could quietly hide broken locking.
- **Mutation-checked.** With the dispatch lock and row locks removed, 6 of the 12 concurrency tests fail, including the 20-for-5 test at both isolation levels. With the topic re-check removed, its test fails.
- **Beyond the test suite, in Docker:** load tests at up to 256 concurrent clients, a 100,000-request backlog, cold starts and killing an instance, each followed by checking the invariants in SQL.
- **xUnit v3** on Microsoft.Testing.Platform, with assertions from xUnit itself. FluentAssertions now needs a commercial licence for commercial use.
- **CI** builds, runs every test on Linux with Testcontainers, checks migrations are up to date, and builds the image.

## Known limits

Stated deliberately, so they can be discussed rather than discovered.

- **Assignments are serial across the whole system**, about 100 per second on the test laptop. That is far above what human experts can absorb, which is why it is the right trade here. If it ever became the limit, the next step is one lock per group of experts that share topics, and to measure first.
- **Request latency grows with concurrency**, because callers wait for their turn: p50 about 30ms at 4 concurrent clients, about 400ms at 64 and about 2s at 256. Moving dispatch to a background worker would make submission instant, but clients would no longer see the assignment in the response.
- **A waiting dispatcher holds a pooled connection.** A burst of 256 clients produced no errors. A burst past the pool's limits would fail some dispatches, which are logged and finished by the sweeper. The dispatch lock waits at most 20 seconds.
- **Commit lost during an insert retry.** If the connection drops just after a successful commit, the retry sees the unique index and reports `200` rather than `201`, and the request waits for the sweeper for up to 30 seconds. The data is correct.
- **Clocks.** Ranks use the application's clock, so instances should be time-synced. Skew of a few seconds is negligible next to a 30-minute head start.
- **Deactivating an expert doesn't reassign their open requests.** The task doesn't ask for this, and it is a product decision.
- **Out of scope:** authentication and authorization, which are needed before any real use, plus metrics and tracing beyond structured logs and health checks.
