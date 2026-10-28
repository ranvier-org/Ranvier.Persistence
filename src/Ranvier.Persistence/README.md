# Ranvier.Persistence

Keyed persisted inputs for Ranvier graphs. A persisted signal or keyed collection is restored from an `IStateStore`
loaded before the graph is built, and saved back to it after each flush that changes it. Derived nodes are not stored: they
recompute from the restored inputs.

Preview API. The package holds three stores: an in-memory store, a file store (.NET, written to a temporary file and
renamed) and a `localStorage` store (Fable).

```fsharp
open Ranvier
open Ranvier.Persistence

let count = Persist.key "count" (PersistCodec.ofJournal intCodec)

task {
    let! session = Persist.loadAsync (FileStateStore "state") [ count ]
    use graph = new Graph ()
    use _active = graph.Activate ()
    let clicks = Persist.signal session count 0   // the stored value, else 0
    clicks.Value <- clicks.Value + 1               // saved after this flush
    do! session.FlushAsync ()                      // completes once the store has the write
}
```

- A key is declared with its codec: a `JournalCodec<'T>` plus ordered migrations. `loadAsync` fails with a
  `PersistLoadException` naming the key on a newer stored version, a missing or failed migration, or a decode
  failure; `PersistOptions.DiscardUndecodable` uses the fallback for a decode failure instead.
- `PersistOptions.Buster` discards every stored record when the stored buster differs.
- The changes of one flush save in one `SaveMany` when the graph's journal is `session.Journal`; `FlushAsync`
  completes after every earlier save.
- `Persist.rows` and `Persist.document` persist a keyed collection by row or as one record, in insertion order.
- `Persist.restore` and `Persist.track` persist an `Mvu` model or any tracked read.
- `session.Journal` records the input-journal position each save includes; a reload exposes it as
  `session.Position`.
- `Persist.sourceKey` and `Persist.source` persist an async source's settled value or failure message.
- `Persist.replay` replays the journal entries after `session.Position` on a restored graph, in either replay
  mode.

Persisted inputs belong at the root of a graph or in a keyed factory. The trace dump is not a persistence format.
Guide: [Persisted inputs](https://ranvier-org.github.io/Ranvier.Persistence/guide/persistence/).
