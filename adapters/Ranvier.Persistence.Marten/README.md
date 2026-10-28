# Ranvier.Persistence.Marten

Marten/PostgreSQL storage for `Ranvier.Persistence`, targeting .NET 10. The adapter is a separate package; Marten does
not become a dependency of the core or persistence packages.

```fsharp
open Marten
open Ranvier.Persistence
open Ranvier.Persistence.Marten

use database = DocumentStore.For connectionString
let store = MartenStateStore(database, "customer-42") :> IStateStore
```

The caller owns `database`. Each operation uses a fresh session. The nonblank scope is a Marten tenant:
loads, deletes and `Clear` affect that scope only. Use a scope per application state partition, not per call.
Marten's normal schema management applies to the adapter's `MartenStateDocument` mapping.

- `SaveMany` commits a whole batch in one PostgreSQL transaction.
- `ETag = None` unconditionally inserts or replaces a record.
- A loaded record has an opaque ETag. Pass it back to require that version: stale or deleted records fault the
  batch with `JasperFx.ConcurrencyException`, possibly wrapped in `AggregateException`. Reload after a successful
  conditional save to get its new ETag.
- Duplicate keys in a save and malformed ETags raise `ArgumentException` before committing.
- Keys are case-sensitive; empty keys round-trip at the store level. Keys must be valid Unicode.
- `PersistSession` currently writes `None`, so ordinary persisted signals retain last-writer-wins behaviour.
  Explicit conditional saves are available through `IStateStore`.

For a graph round trip and conditional updates, see the
[Marten guide](https://ranvier-org.github.io/Ranvier.Persistence/guide/persistence-marten/).

## Integration tests

Use a disposable PostgreSQL database. Set `RANVIER_MARTEN_CONNECTION`, then run:

```shell
dotnet test tests/Ranvier.Persistence.Marten.Tests -c Release
```

The suite reuses the shared persistence conformance cases and optimistic-store cases. It also checks competing
writers, atomic rollback, tenant isolation, unusual keys and a graph restart. Missing configuration or an
unavailable database fails the suite.

The build CLI's ordinary test pipeline excludes this integration project; CI runs it separately with PostgreSQL.
