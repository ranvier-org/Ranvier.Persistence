---
title: Restore from a journal
order: 31
---

Restores build on the core library's [journal](https://ranvier-org.github.io/Ranvier/guide/journal/) and its
[replay modes](https://ranvier-org.github.io/Ranvier/guide/journal/#replay).

A restore rebuilds a graph from a stored-state snapshot and replays the journal entries recorded
after it. The snapshot is the store of a persist session from
[Ranvier.Persistence](persistence.md): with the graph's journal set to the session's wrapper,
each save stores the records and the `Seq` of the last journal entry they include, the snapshot's
position.

```fsharp
let source = Persist.sourceKey "results" (PersistCodec.ofJournal resultsCodec)

// The recorded run: every keyed input persisted under its journal key.
let! session = Persist.loadAsync store [ query; source ]
use graph = new Graph (GraphOptions.Default.WithJournal (session.Journal journal))
use _active = graph.Activate ()
let q = Journal.key "query" textCodec (Persist.signal session query "")
let results = Journal.keySource "results" resultsCodec (Persist.source session source)

// The restore: a session loaded from the snapshot, the same graph code, then the tail.
let! restored = Persist.loadAsync snapshot [ query; source ]
use target = new Graph (GraphOptions.Default.WithJournal (restored.Journal (MemoryJournal "app")))
use _active = target.Activate ()
// ... build the inputs as above, from `restored` ...
Persist.replay ReplayMode.Diagnostic restored target journal.Entries
```

- `Persist.source` restores an async source: a settled value, or a failure as a `PersistedFailure`
  carrying the stored message, the way a replayed failure is a `JournalReplayedFailure`. A source
  with no record starts pending. The restored status is published inside `Journal.derived`, so the
  restore records no entry, and every reader sees the stored status first.
- `Persist.replay mode session graph entries` replays the entries with a `Seq` greater than
  `session.Position` through `Journal.replay mode`, in either mode. With no stored position it
  replays every entry. The entries must hold the input at the position; otherwise it raises
  `ArgumentException`.
- An input of work started at or before the position is applied as an `External` entry in both
  modes. Work started after the position re-runs and is matched as in any replay.
- The restored graph's journal records the replayed entries from its own next `Seq`, so a position
  saved after a restore refers to the restored graph's journal.

After a restore, every keyed input and every memo equals the uninterrupted run's, in both modes,
for runs of external writes, settles and failures.

The v1 limits:

- Every keyed input of the restored graph is persisted under its journal key. An input without a
  record restarts from its fallback.
- Setup work, the work a graph starts before its first input, re-runs when the restored graph is
  built, as it does on any reload. An input of setup work recorded after the position cannot be
  applied once, so `Persist.replay` raises `JournalReplayException` on it before applying any
  entry.
- Work running at the snapshot can start again as setup work of the restored graph, such as work
  an effect over persisted state starts, and the re-run work produces its input again. When the
  tail holds an input of work started at or before the position and the restored graph started
  setup work other than an effect's run (`Journal.work`, an `Mvu` command), `Persist.replay`
  raises `JournalReplayException` on that input before applying any entry. On .NET, an input from
  a continuation of an effect's body that the restored graph re-runs is applied twice.
- Two sessions busted to a new buster over one store can clear each other's saves: see
  [Busting the store](persistence.md#busting-the-store). A snapshot holds what the store held at
  the copy.
