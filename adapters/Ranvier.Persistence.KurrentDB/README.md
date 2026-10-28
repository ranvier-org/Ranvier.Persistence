# Ranvier.Persistence.KurrentDB

Small scoped snapshots with atomic batches and optional record ETags.

Reference this project from a .NET 10 application:

```fsharp
open KurrentDB.Client
open Ranvier.Persistence
open Ranvier.Persistence.KurrentDB

use client = new KurrentDBClient(KurrentDBClientSettings.Create connectionString)
let store = KurrentDBStateStore(client, "customer-42") :> IStateStore
```

The caller owns the client. Scopes are independent, case-sensitive Unicode strings of up to 512 UTF-8 bytes.
The adapter owns each scope's `ranvier-state-v1-` stream.

- Every mutation appends the complete scope, limited to **1 MiB of encoded JSON**.
- Save batches commit atomically. Duplicate keys, malformed tags and invalid Unicode are rejected.
- A loaded ETag supports conditional saves. A changed or absent record raises `KurrentDBStateConflictException`;
  reload after successful saves for new tokens. Unrelated records retain their ETags.
- A missing ETag permits an unconditional insert or replacement. `PersistSession` currently writes this way.
- A mutation attempts up to 32 appends on stream conflicts, waiting 1–50 ms between attempts before reading a fresh snapshot.
  Other failures propagate.
- Delete and clear remove current records within the scope. Historical snapshot events remain.
- Reserve the stream for this adapter. External writes, physical deletion and age-based expiry can invalidate state.

Use this adapter for small snapshots. Writes copy all scoped records, and history grows with every mutation.
Manage retention explicitly; retain at least the latest event.
A connection failure can leave the commit acknowledgement uncertain; reload before retrying a conditional write.

The [KurrentDB guide](https://ranvier-org.github.io/Ranvier.Persistence/guide/persistence-kurrentdb/) includes setup and a persisted
signal round-trip. The adapter adds KurrentDB.Client only to this package; core persistence stays independent.

Run the integration suite against a disposable server:

```shell
# Set RANVIER_KURRENTDB_CONNECTION first.
dotnet test tests/Ranvier.Persistence.KurrentDB.Tests -c Release
```

A missing connection or unavailable server fails the tests. CI runs both package variants against its own
pinned container; the ordinary build CLI excludes this integration project.
