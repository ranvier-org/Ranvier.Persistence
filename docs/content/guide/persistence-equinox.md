---
title: Equinox / FsCodec sample
order: 29
---

The [source sample](https://github.com/ranvier-org/Ranvier.Persistence/tree/main/samples/Ranvier.Persistence.Equinox)
implements `IStateStore` with Equinox and FsCodec. It uses Equinox's gRPC EventStoreDB backend,
tested against KurrentDB 26.1.1.3690. The sample targets .NET 10 and keeps backend dependencies outside
Ranvier's core and persistence packages.

## What each library does

![Ranvier supplies input records, Equinox checks their tokens, FsCodec encodes a snapshot, and KurrentDB appends it atomically.](/Ranvier.Persistence/diagrams/persistence-equinox.svg)

- **Ranvier** encodes persisted inputs with the application's codecs and restores them into a graph.
- **Equinox** loads the current scope, runs a decision and commits at the expected stream revision.
  A competing append reloads state and reruns that decision.
- **FsCodec** maps the versioned `SnapshotV1` event to an event type and JSON body.
- **KurrentDB** durably stores the event stream. Equinox can use other backends with different category wiring;
  this sample tests the EventStoreDB backend.

The decision validates every supplied record ETag again after a retry. A stale record fails instead of
overwriting the winning value. Unconditional writes merge into the freshly loaded scope.

## Run the round trip

Start a disposable server with the [KurrentDB installation guide](https://docs.kurrent.io/server/v26.2/quick-start/installation).
Set the connection, then run the sample:

```powershell
$env:RANVIER_EQUINOX_CONNECTION = "esdb://localhost:2113?tls=false"
dotnet run --project samples/Ranvier.Persistence.Equinox -c Release -- customer-42
```

The connection uses `esdb://` because Equinox.EventStoreDb depends on EventStore.Client.Grpc.Streams.
The local insecure connection above should be replaced with the server's TLS and authentication settings
for production.

Expected output:

```text
Restored count=21; doubled=42
```

The executable creates a persisted signal, sets it to 21 and awaits `FlushAsync`. It then loads the stored
inputs into a fresh graph, where a memo recomputes 42. Graph work runs synchronously; the queued save
continues after graph disposal and is awaited before reloading.

## Use it in an application

Reference `samples/Ranvier.Persistence.Equinox/Ranvier.Persistence.Equinox.fsproj`, or copy `StateStore.fs`
with its package references. This is a source sample rather than a published adapter package.

```fsharp
open EventStore.Client
open Ranvier.Persistence
open Ranvier.Samples.EquinoxFsCodec

use client = new EventStoreClient(EventStoreClientSettings.Create connectionString)
let store = EquinoxStateStore(client, "customer-42") :> IStateStore
```

With a project reference, `Ranvier.Samples.EquinoxFsCodec.Program.roundTrip store` returns a task
completing with `(21, 42)`. Copying only `StateStore.fs` provides the store implementation.

Keep the caller-owned client for the application's lifetime. The sample uses an uncached Equinox Decider
and the current Serilog logger. Use a stable scope to reload the same inputs.

See [Persisted inputs](persistence.md) for keys, codecs, migrations and busters.

## Conditional writes

Ordinary `PersistSession` writes currently carry `ETag = None`, which permits an unconditional insert
or replacement. Preserve a loaded record's ETag for an explicit conditional write:

```fsharp
let incrementStored () =
    task {
        let! records = store.LoadMany [ "count" ]
        let current = records.Head
        do! store.SaveMany [ { current with Payload = string (int current.Payload + 1) } ]
        return! store.LoadMany [ "count" ]
    }
```

Run the round trip first to seed `"count"`. If another writer changes or deletes that record,
`EquinoxStateConflictException.Key` identifies the rejected key. Task-to-async bridges may wrap the
exception in `AggregateException`. Reload and resolve the conflict in the application.

A successful save changes the saved records' ETags; unrelated records retain theirs.
`StoredRecord.Version` remains the payload codec version. Reload to get new tokens.

## Scope, capacity and history

- Each scope has one stream: category `ranvierEquinoxV1`, followed by a base64url UTF-8 scope identifier.
  Scopes are case-sensitive, valid Unicode strings of at most 512 UTF-8 bytes.
- Every mutation writes the complete scope in a `SnapshotV1` event, limited to **1 MiB of encoded JSON**.
  This suits small persisted state. The server may impose a lower limit.
- One `SaveMany` commits all its records atomically. Duplicate keys, malformed tags and invalid Unicode are rejected.
- Equinox permits at most **32 append attempts** under stream contention. Exhausted retries and connection errors
  propagate. A connection failure can leave acknowledgement uncertain; reload before retrying a conditional write.
- Delete and clear remove current records by appending a snapshot. Historical events remain.
  These operations are logical deletion.
- Reserve these streams for the sample. Unknown or corrupt latest events fail reads and writes.
  Physical deletion, truncation and age-based expiry can invalidate state. Manage retention explicitly and
  retain at least the latest snapshot.

Adapt the event contract and Equinox access strategy to an application's storage needs. Journal transports
remain separate work under [#185](https://github.com/ranvier-org/Ranvier/issues/185).

## Integration tests

Set `RANVIER_EQUINOX_CONNECTION` to a disposable server:

```shell
dotnet test tests/Ranvier.Persistence.Equinox.Tests -c Release
```

The suite covers shared storage/ETag contracts, independent clients, atomic rollback, concurrent distinct-key
writes, repeated snapshots, readable FsCodec events, corrupt heads, capacity limits and graph restart.
A missing or unavailable server fails the run. CI tests both trace variants and runs the executable.
