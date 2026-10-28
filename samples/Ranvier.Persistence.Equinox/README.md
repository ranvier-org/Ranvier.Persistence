# Equinox and FsCodec persistence sample

A source sample implementing Ranvier's IStateStore with Equinox.EventStoreDb and FsCodec.SystemTextJson.
The integration suite runs against KurrentDB 26.1.1.3690 using Equinox's gRPC EventStoreDB backend.
The core packages acquire no backend or serializer dependency.

## Run it

Start a disposable KurrentDB server, then set the connection in PowerShell:

```powershell
$env:RANVIER_EQUINOX_CONNECTION = "esdb://localhost:2113?tls=false"
dotnet run --project samples/Ranvier.Persistence.Equinox -c Release -- customer-42
```

Use the `esdb://` scheme required by EventStore.Client.Grpc.Streams. The insecure connection above is for
local development; production connections use the server's TLS and authentication settings.

The executable saves a signal, reloads inputs into a fresh graph and prints:

```text
Restored count=21; doubled=42
```

## Use the store

Reference the sample project, or copy StateStore.fs into an application with the same package references:

```fsharp
open EventStore.Client
open Ranvier.Persistence
open Ranvier.Samples.EquinoxFsCodec

use client = new EventStoreClient(EventStoreClientSettings.Create connectionString)
let store = EquinoxStateStore(client, "customer-42") :> IStateStore
```

The caller owns the client. The sample uses an uncached Equinox Decider and the current Serilog logger.
FsCodec encodes the public `Events.Event` contract. Each `SnapshotV1` event contains all current records.
Equinox appends against the loaded stream revision and reruns the decision on a competing append.

- `ETag = None` inserts or replaces a key. Ordinary PersistSession writes currently use this mode.
- An explicit ETag requires a live record with that token. Stale or deleted records raise
  `EquinoxStateConflictException`; reload after successful saves to obtain new tokens.
- Every save batch commits atomically. Retried decisions recheck all supplied tokens against fresh state.
- Scopes accept at most 512 UTF-8 bytes. Encoded snapshots are limited to 1 MiB.
- Stream contention permits at most 32 append attempts; exhausted retries and connection errors propagate.
- Streams use category `ranvierEquinoxV1` and a base64url scope identifier. Reserve these streams for the sample.
- Delete and clear remove current records but retain historical events. Every mutation appends a complete snapshot.
  Manage retention explicitly and retain at least the latest event; age-based expiry can remove idle current state.
- A connection failure can leave acknowledgement uncertain. Reload before retrying a conditional write.

This sample targets small persisted state. Adapt the event contract, access strategy and category wiring
for an application's requirements; other Equinox backends require their own integration tests.

See the [guide](https://ranvier-org.github.io/Ranvier.Persistence/guide/persistence-equinox/) for the data flow and conditional-write example.

## Test

```shell
dotnet test tests/Ranvier.Persistence.Equinox.Tests -c Release
```

Set RANVIER_EQUINOX_CONNECTION first. A missing or unavailable server fails the run.
The shared storage/ETag suite and backend-specific tests run in CI against a pinned real server.
The project is marked as an integration suite and excluded from ordinary build CLI tests.
