---
title: KurrentDB snapshots
order: 28
---

`Ranvier.Persistence.KurrentDB` stores a small scope of persisted inputs as a snapshot stream.
Each mutation appends the full scope atomically. Reads fetch the latest snapshot.

## Setup

Install the .NET 10 adapter from NuGet:

```shell
dotnet add package Ranvier.Persistence.KurrentDB --version 0.1.0-preview.2
```

```fsharp
open KurrentDB.Client
open Ranvier.Persistence
open Ranvier.Persistence.KurrentDB

use client = new KurrentDBClient(KurrentDBClientSettings.Create connectionString)
let store = KurrentDBStateStore(client, "customer-42") :> IStateStore
```

Keep the caller-owned client for the application's lifetime. Use a stable scope to reload the same state.
Scopes are case-sensitive Unicode strings, limited to 512 UTF-8 bytes; each has an independent stream.
The adapter creates its stream on the first mutation.

For local tests, start an insecure server using the [KurrentDB installation guide](https://docs.kurrent.io/server/v26.2/quick-start/installation)
and connect with `kurrentdb://localhost:2113?tls=false`. Production connections should use the server's
TLS and authentication settings.

## Save, reload, derive

```fsharp
open Ranvier

let codec: JournalCodec<int> =
    { Name = "int"
      Version = 1
      Encode = string
      Decode = fun text ->
          match System.Int32.TryParse text with
          | true, value -> Ok value
          | _ -> Error "Expected an integer" }

let countKey = Persist.key "count" (PersistCodec.ofJournal codec)

let writeCount session value =
    use graph = new Graph()
    use active = graph.Activate()
    let count = Persist.signal session countKey 0
    count.Value <- value
    session.FlushAsync()

let readCount session =
    use graph = new Graph()
    use active = graph.Activate()
    let count = Persist.signal session countKey 0
    let doubled = createMemo (fun _ -> count.Value * 2)
    count.Value, doubled.Value

let roundTrip () =
    task {
        let! session = Persist.loadAsync store [ countKey ]
        do! writeCount session 21
        let! restored = Persist.loadAsync store [ countKey ]
        return readCount restored
    }
// roundTrip() completes with (21, 42).
```

The graph work runs synchronously. Await the returned flush task before reloading; queued storage continues
after graph disposal. See [Persisted inputs](persistence.md) for codecs, migrations and busters.

## Conditional writes

Loaded records carry an opaque ETag. Preserve it to reject a competing update:

```fsharp
let incrementStored () =
    task {
        let! rows = store.LoadMany [ "count" ]
        let current = rows.Head
        do! store.SaveMany [ { current with Payload = string (int current.Payload + 1) } ]
        return! store.LoadMany [ "count" ]
    }
```

Seed `"count"` with the round-trip example first. A changed or deleted record raises
`KurrentDBStateConflictException`, whose `Key` identifies the conflict. Task-to-async bridges may wrap it
in `AggregateException`. Reload and resolve the conflict in the application.

A successful save changes the saved records' ETags; unrelated records keep theirs. `StoredRecord.Version`
remains the payload codec version. Reload for the new ETag before the next conditional save.

Ordinary `PersistSession` writes currently carry `ETag = None` and use last-writer-wins.
Automatic optimistic session writes remain separate work.

## Capacity and lifecycle

- Every save, delete or clear appends a complete snapshot. The encoded scope is limited to **1 MiB**;
  oversized mutations fail before appending. The server may impose a lower limit.
- A `SaveMany` batch commits completely or fails. Duplicate keys, malformed ETags and invalid Unicode are rejected.
- A mutation attempts up to **32 appends** against fresh snapshots on stream conflicts. A stale record ETag
  fails immediately. Exhausted stream conflicts and connection errors propagate to the caller.
- A missing ETag permits an insert or replacement. Deletes and clear are unconditional within the scope.
- `Clear` and `DeleteMany` remove records from the current snapshot. Older events remain in stream history;
  these operations are logical deletion.
- Reserve streams beginning `ranvier-state-v1-` for the adapter. Its scope suffix is base64url of the UTF-8 scope.
  External events, physical deletion, truncation or expiry can invalidate stored state.
- History grows with each mutation. Manage retention explicitly; a maximum-count policy retaining at least
  the latest event can preserve current state. Age-based expiry can remove an idle scope's current snapshot.

For larger state or frequent writes, evaluate the [Marten adapter](persistence-marten.md).
A connection failure can leave the commit acknowledgement uncertain. Reload before retrying a conditional write.
The KurrentDB adapter implements `IStateStore`; journal transport remains part of
[#185](https://github.com/ranvier-org/Ranvier/issues/185).

## Integration tests

Set `RANVIER_KURRENTDB_CONNECTION` to a disposable server, then run:

```shell
dotnet test tests/Ranvier.Persistence.KurrentDB.Tests -c Release
```

The suite covers the shared storage and ETag contracts, atomic rollback, concurrent clients, scope isolation,
capacity failures, corrupt snapshots and a fresh graph restart. A missing or unavailable server fails the run.
CI starts its own pinned KurrentDB container and tests both package variants.
