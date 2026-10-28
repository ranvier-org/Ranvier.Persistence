---
title: Persisted inputs
order: 26
---

:::info
Preview — `Ranvier.Persistence` is pre-release; its APIs and stored record format may change.
:::

`Ranvier.Persistence` restores input state before building a graph, then saves changes after
each flush. Persist signals and keyed collections; memos and effects recompute from those inputs.
An `IStateStore` supplies memory, file, browser storage or an application-defined store.

For PostgreSQL, use the separate [Marten adapter](persistence-marten.md), with scoped records and optional ETag checks.
For small snapshots in KurrentDB, use the [KurrentDB adapter](persistence-kurrentdb.md).
The [Equinox/FsCodec sample](persistence-equinox.md) shows an event-sourced implementation over KurrentDB.

```fsharp
open Ranvier
open Ranvier.Persistence

let countCodec: JournalCodec<int> =
    { Name = "int"
      Version = 1
      Encode = string
      Decode = fun text ->
          match System.Int32.TryParse text with
          | true, value -> Ok value
          | _ -> Error ("not an int: " + text) }

let count = Persist.key "count" (PersistCodec.ofJournal countCodec)

task {
    let! session = Persist.loadAsync (FileStateStore "state") [ count ]
    use graph = new Graph ()
    use _active = graph.Activate ()

    let clicks = Persist.signal session count 0       // the stored value, else 0
    let doubled = createMemo (fun _ -> clicks.Value * 2) // recomputes; never stored

    clicks.Value <- clicks.Value + 1                   // saved after this flush
    do! session.FlushAsync ()                          // completes once the store has the write
}
```

## Keys and codecs

A key is declared once, with its codec, and passed to the load. `loadAsync` decodes every declared
key before the graph exists, so a record that cannot be restored fails the load, not the first read.

A persist codec is the journal's `JournalCodec<'T>` (see [Keys and codecs](https://ranvier-org.github.io/Ranvier/guide/journal/#keys-and-codecs))
plus ordered migrations. A journaled, persisted signal uses one codec value for both. A Ranvier.Wire
codec adapts as the journal guide shows:

```fsharp
let noteCodec (wire: WireCodec<Note>) : PersistCodec<Note> =
    PersistCodec.ofJournal
        { Name = "note"
          Version = 2
          Encode = wire.Encode
          Decode = wire.Decode >> Result.mapError string }
    |> PersistCodec.withMigration 1 (fun v1 -> Ok (upgradeNote v1))

let note = Persist.key "note" (noteCodec noteWire)
```

Each record stores the codec's `Version`. A record at an older version runs the migrations from its
version up, in order; `withMigration from` migrates a payload from `from` to `from + 1`. The load
fails with `PersistLoadException`, whose message and `Key` name the key, when:

| `Failure`          | The stored record                                   |
|--------------------|-----------------------------------------------------|
| `NewerVersion`     | is at a version greater than the codec's            |
| `MissingMigration` | is at a version the codec has no migration from     |
| `MigrationFailed`  | is at a version whose migration returned an error   |
| `DecodeFailed`     | is at the codec's version, and `Decode` returned an error |

`Persist.loadWith { PersistOptions.defaults with DiscardUndecodable = true }` discards a record that
fails with `DecodeFailed` and uses the fallback for its key. The other failures still fail the load.

The load takes an `IPersistKey list`, so value keys and collection keys of different types share one
list: `Persist.loadAsync store [ count; note; todos ]`. Two keys with one store key raise
`ArgumentException`, as does an empty key or one beginning `$ranvier/` (`Persist.ReservedPrefix`),
which is reserved for the session's own records.

### Busting the store

`PersistOptions.Buster` is a store-wide version. When the stored buster differs, every record of the
load reads as absent and each key uses its fallback. Each save of the session clears the store and
writes the new buster, until one such save succeeds; a later load then reads the new buster and the
records saved since. A save skips the clear when the store already holds the session's buster, so
sessions busted to the same buster keep each other's saves.

```fsharp
let! session = Persist.loadWith { PersistOptions.defaults with Buster = Some "2026-10" } store keys
```

The last writer wins. Two sessions over one store, such as two browser windows over one
`localStorage`, each save their own writes in their own order. When both are busted to a new buster,
a save by one session can clear records the other saved a moment earlier: the clear and the write of
the buster are separate store calls. Version 1 accepts this race; the next load reads whatever the
store then holds.

## Where persisted inputs belong

A session serves one graph: its first persisted input attaches it to the current graph, and a
persisted input made in another graph raises `InvalidOperationException`. So does a persisted input,
or a `Persist.restore`, for a key the load was not given.

A persisted input belongs at the root of the graph, or in a keyed factory with one declared key per
row. One live persisted input holds a key: a second `Persist.signal` for a key whose first is still
alive raises `InvalidOperationException`. When its owner is disposed, the key is free; a persisted
input created for it again starts from the key's last written value, not from the stored value of
the load. A keyed factory row removed and added back therefore keeps its edits.

A persisted input inside a branch that re-runs, such as the body of a memo, is re-created on every
run. Keep it at the root and read it from the branch.

## Collections

`Persist.rows` and `Persist.document` declare a keyed collection, and `Persist.collection` makes a
`KeyedCollection` restored from it, in the stored insertion order.

```fsharp
let todos = Persist.rows "todos" todoCodec _.Id string   // one record per row
let todoDoc = Persist.document "todos" todoCodec _.Id    // one record for the collection

let items = Persist.collection session todos
items.AddOrUpdate { Id = 3; Text = "milk" }              // saves the row "todos/3" and the index
```

| Layout     | Records                                            | A one-row edit saves     |
|------------|----------------------------------------------------|--------------------------|
| `rows`     | one per row under `key/rowKey`, and an index under `key` | that row's record   |
| `document` | one under `key` holding every row                  | the one document record  |

Adding or removing a row of the row layout also saves the index; removing deletes the row's record.

## Saving and durability

Every persisted input of a session that changes in one flush is saved in one `SaveMany`, after the
flush's effects have run, when the graph's journal is the session's [journal wrapper](#journal-position).
Without the wrapper, a persisted input written by an effect that runs after the session's save starts
is saved in a second `SaveMany` of the flush. A key written twice in one flush is saved once, with its
last value. Saves run one after another, in write order.

`session.FlushAsync ()` completes once every save started before the call has completed, and faults
with the exception of a save that faulted since the previous `FlushAsync`. A faulted save does not
stop later saves. Await it before the process exits, or before a test reloads the store.

## Mvu models

An `Mvu` model persists as one key: restore the initial model, then track the model.

```fsharp
let modelKey = Persist.key "app/model" modelCodec

let app = Mvu.create (Persist.restore session modelKey init) update
Persist.track session modelKey (fun () -> app.Model)
```

`Persist.restore` returns the key's last written value, else its stored value, else the fallback.
`Persist.track` saves any tracked read after each flush that changes it, until the current owner is
disposed. Its first value is saved only when it differs from the key's restored value; with nothing
restored, the value at the call is not saved.

## Stores

| Store                    | Platform | Records                                                     |
|--------------------------|----------|-------------------------------------------------------------|
| `MemoryStateStore`       | both     | a dictionary in process memory; every task has finished     |
| `FileStateStore dir`     | .NET     | one `.record` file per key, written to a temporary file then renamed over the key's file |
| `LocalStorageStateStore prefix` | Fable | one `localStorage` item per key under `prefix`; `Clear` removes the prefix's items only |

`LocalStorageStateStore (prefix, storage)` takes another `IWebStorage`, the five Web Storage
operations the store uses, in place of the browser's `localStorage`. Each
store implements `IStateStore` (`LoadMany`, `SaveMany`, `DeleteMany`, `Clear`), and a custom store
implements the same four members. A `StoredRecord` holds the `Key`, the codec `Version` its
`Payload` was encoded at, and an `ETag` carried for stores that check one; the three stores ignore it.

`MemoryStateStore.Records` lists the stored records, so a test can assert what a flush saved.
`PersistCodec.encode codec key value` builds the record a session would save, and
`PersistCodec.restore codec record` migrates and decodes one into `Ok value` or `Error` with a
`PersistFailure`, as the load does:

```fsharp
let store = MemoryStateStore ()
let codec = PersistCodec.ofJournal countCodec
do! (store :> IStateStore).SaveMany [ PersistCodec.encode codec "count" 41 ]
let restored = PersistCodec.restore codec store.Records.Head // Ok 41
```

## Journal position

A session can record where an input journal stood when it saved. Build the graph with the session's
journal wrapper, and each save also stores the `Seq` of the last journal entry its records include:

```fsharp
let! session = Persist.loadAsync store keys
use graph = new Graph (GraphOptions.Default.WithJournal (session.Journal journal))
// after a reload: session.Position is the Seq the stored records include
```

`session.Position` is the stored position, or `None`. `Persist.replay mode session graph entries`
replays the journal entries after it on a graph restored from the session, in diagnostic or
re-enactment mode: see [Restore](journal-restore.md). A position refers to the journal of the graph
that saved it; after a restore, the restored graph's journal numbers the replayed entries afresh.

An async source persists with `Persist.sourceKey` and `Persist.source`. Its record holds the
settled value, or the failure's message, which restores as a `PersistedFailure`; a pending source
has no record. A restore covers every keyed input only when each is persisted under its journal
key.

```fsharp
let results = Persist.sourceKey "results" (PersistCodec.ofJournal resultsCodec)
let! session = Persist.loadAsync store [ results ]
let source = Journal.keySource "results" resultsCodec (Persist.source session results)
```

- `Persist.sourceKey key codec` raises `ArgumentException` on an empty key or one beginning with
  `$ranvier/`. The record's version is the codec's, and the codec's migrations apply to the stored
  value.
- `Persist.source session key` makes the source in the current graph and owner: settled with the
  restored value, failed with the restored failure, or pending when the key has no record. The
  restored status is published inside `Journal.derived`, so it records no journal entry.
- After each flush that changes the source's status, the new status is saved to the store; a
  return to pending deletes the record.
- Register the source with `Journal.keySource` under the same key, so the journal records its later
  settles and failures and a restore can replay them.

## What is not stored

Derived nodes recompute: a restored graph's memos equal those of a run that was never interrupted,
and no effect observes the fallback before the stored value.

The trace dump is not a persistence format. It describes a traced build's graph for inspection, it
changes between releases, and an untraced build has none. Persist inputs with this package, and
record them with the [input journal](https://ranvier-org.github.io/Ranvier/guide/journal/).
