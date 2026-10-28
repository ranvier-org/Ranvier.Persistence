---
title: Marten / PostgreSQL
order: 27
---

`Ranvier.Persistence.Marten` stores persisted inputs in PostgreSQL through Marten. It targets .NET 10 and
keeps the database client in a separate adapter package. See [Persisted inputs](persistence.md) for keys,
codecs and migrations.

## Setup

Install the .NET 10 adapter from NuGet:

```shell
dotnet add package Ranvier.Persistence.Marten --version 0.1.0-preview.1
```

```fsharp
open Marten
open Ranvier.Persistence
open Ranvier.Persistence.Marten

use database = DocumentStore.For connectionString
let store = MartenStateStore(database, "customer-42") :> IStateStore
```

Keep `database` for the application's lifetime. Each adapter operation opens and disposes its own session.
The scope is a Marten tenant: two scopes can use `"count"` independently, and `Clear` removes only the
chosen scope's records. Use a stable scope for the state you want to reload.

The document's mapping enables tenant isolation and version metadata automatically. Marten's configured
schema-management policy controls table creation; provision the mapping before using a database that
disables automatic schema changes.

## Save, reload, derive

This example saves one input, reloads it and recomputes a memo on a fresh graph:

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

`writeCount` builds and changes the graph synchronously. Its returned task acknowledges the already
queued save, which continues after the graph is disposed. Await that task before reloading.

## Conditional writes

Ordinary `PersistSession` writes currently carry `ETag = None`, so they use last-writer-wins.
For an explicit conditional update, keep the ETag returned by `LoadMany`:

```fsharp
let incrementStored () =
    task {
        let! rows = store.LoadMany [ "count" ]
        let current = rows.Head
        let next = { current with Payload = string (int current.Payload + 1) }
        do! store.SaveMany [ next ]
        return! store.LoadMany [ "count" ]
    }
```

Seed `"count"` with the round-trip example first. If another writer changes or deletes that record before
the save commits, the conditional save faults. The batch rolls back, including unconditional records in it.
Marten reports `JasperFx.ConcurrencyException`, which may be wrapped in an `AggregateException`.
Reload and resolve the conflict in the application; the adapter does not retry a stale update.

A successful save changes the ETag. Reload before the next conditional save. `StoredRecord.Version`
is the codec version and changes only when your payload format changes; the ETag tracks database writes.

## Storage behaviour

- One `SaveMany` is one PostgreSQL transaction.
- A record with `ETag = None` inserts or replaces its key. `Some tag` requires an existing matching version.
- `DeleteMany` and `Clear` are unconditional and scoped to the tenant.
- A save containing duplicate keys or a malformed ETag raises `ArgumentException` before committing.
- Keys are case-sensitive Unicode strings. The store also accepts an empty key, although `Persist.key` rejects it.

This adapter implements state storage. A remote journal transport and a combined state/journal transaction
are separate work under [#185](https://github.com/ranvier-org/Ranvier/issues/185).

## Run the integration suite

Point `RANVIER_MARTEN_CONNECTION` at a disposable PostgreSQL database, then run:

```shell
dotnet test tests/Ranvier.Persistence.Marten.Tests -c Release
```

The suite includes the shared store and ETag cases, competing writers, rollback, tenant isolation and graph
restart. It fails when the connection is missing or the database is unavailable.
