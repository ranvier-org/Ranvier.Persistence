namespace Ranvier.Persistence.KurrentDB

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json
open System.Threading.Tasks
open KurrentDB.Client
open Ranvier.Persistence

/// <summary>A persisted record and its concurrency token inside a scoped snapshot.</summary>
[<CLIMutable>]
type internal KurrentRow =
    {
        Key: string
        Version: int
        Payload: string
        Tag: string
    }

/// <summary>A conditional save whose key is absent or has a different stored ETag.</summary>
type KurrentDBStateConflictException(key: string) =
    inherit
        InvalidOperationException(
            "The stored version of key '"
            + key
            + "' has changed."
        )

    /// <summary>The key whose conditional write was rejected.</summary>
    member _.Key = key

/// <summary>Small scoped snapshots in KurrentDB, with atomic batches and optional record ETags.</summary>
/// <remarks>Each mutation appends the full scope. Stream conflicts retry up to 32 attempts; stale record ETags fail immediately.
/// Reload after a successful save to obtain new tokens. The caller owns the client and the reserved stream's lifecycle.</remarks>
type KurrentDBStateStore
    /// <summary>Creates a store backed by an adapter-owned stream for the scope.</summary>
    /// <param name="client">Caller-owned KurrentDB client, shared across operations.</param>
    /// <param name="scope">Non-blank scope containing at most 512 UTF-8 bytes of valid Unicode.</param>
    (client: KurrentDBClient, scope: string) =
    let utf8 = UTF8Encoding (false, true)
    let eventType = "ranvier-state-snapshot-v1"

    let stream =
        if isNull client then
            nullArg (nameof client)

        if String.IsNullOrWhiteSpace scope then
            invalidArg (nameof scope) "Scope must be non-blank."

        let bytes = utf8.GetBytes scope

        if bytes.Length > 512 then
            invalidArg (nameof scope) "Scope must fit in 512 UTF-8 bytes."

        "ranvier-state-v1-"
        + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    let encodeRows (rows: seq<KurrentRow>) =
        use buffer = new MemoryStream ()
        use writer = new Utf8JsonWriter (buffer)
        writer.WriteStartObject ()
        writer.WriteNumber ("Schema", 1)
        writer.WriteStartArray ("Records")

        for row in rows do
            writer.WriteStartObject ()
            writer.WriteString ("Key", row.Key)
            writer.WriteNumber ("Version", row.Version)
            writer.WriteString ("Payload", row.Payload)
            writer.WriteString ("Tag", row.Tag)
            writer.WriteEndObject ()

        writer.WriteEndArray ()
        writer.WriteEndObject ()
        writer.Flush ()
        let bytes = buffer.ToArray ()

        if bytes.Length > 1024 * 1024 then
            invalidOp "The encoded scope exceeds the adapter's 1 MiB snapshot limit."

        bytes

    let decodeRows (data: ReadOnlyMemory<byte>) =
        try
            use document = JsonDocument.Parse data
            let root = document.RootElement

            if root.GetProperty("Schema").GetInt32() <> 1 then
                raise (InvalidDataException ("The snapshot schema is unsupported."))

            let rows = Dictionary<string, KurrentRow>(StringComparer.Ordinal)

            for item in root.GetProperty("Records").EnumerateArray() do
                let row =
                    {
                        Key = item.GetProperty("Key").GetString()
                        Version = item.GetProperty("Version").GetInt32()
                        Payload = item.GetProperty("Payload").GetString()
                        Tag = item.GetProperty("Tag").GetString()
                    }

                let mutable tag = Guid.Empty

                if
                    isNull row.Key
                    || isNull row.Payload
                    || not (Guid.TryParseExact (row.Tag, "D", &tag))
                    || tag = Guid.Empty
                    || not (rows.TryAdd (row.Key, row))
                then
                    raise (InvalidDataException ("The snapshot contains an invalid record."))

            rows
        with
        | :? JsonException as error -> raise (InvalidDataException ("Invalid snapshot JSON.", error))
        | :? KeyNotFoundException as error -> raise (InvalidDataException ("Missing snapshot field.", error))
        | :? InvalidOperationException as error -> raise (InvalidDataException ("Invalid snapshot field type.", error))
        | :? FormatException as error -> raise (InvalidDataException ("Invalid snapshot field value.", error))

    let read () =
        task {
            let result =
                client.ReadStreamAsync (Direction.Backwards, stream, StreamPosition.End, maxCount = 1L)

            let! state = result.ReadState

            if state = ReadState.StreamNotFound then
                return StreamState.NoStream, Dictionary<string, KurrentRow>(StringComparer.Ordinal)
            else
                use cursor = result.GetAsyncEnumerator ()
                let! found = cursor.MoveNextAsync ()

                if not found then
                    raise (InvalidDataException ("The snapshot stream has no readable head."))

                let entry = cursor.Current.OriginalEvent

                if entry.EventType <> eventType then
                    raise (InvalidDataException ("The stream contains an unsupported event type."))

                let rows = decodeRows entry.Data
                return StreamState.StreamRevision (entry.EventNumber.ToUInt64 ()), rows
        }

    let mutate apply =
        SnapshotRetry.run (fun attempts -> Task.Delay (min 50 (attempts * attempts))) (fun () ->
            task {
                let! expected, rows = read ()
                apply rows
                let data = EventData (Uuid.NewUuid (), eventType, encodeRows rows.Values)
                let! _ = client.AppendToStreamAsync (stream, expected, [ data ])
                return ()
            })

    let validateText name (value: string) =
        if isNull value then
            nullArg name

        utf8.GetByteCount value |> ignore

    interface IStateStore with
        member _.LoadMany keys =
            task {
                for key in keys do
                    validateText "keys" key

                if keys.IsEmpty then
                    return []
                else
                    let! _, rows = read ()

                    return
                        keys
                        |> List.distinct
                        |> List.choose (fun key ->
                            match rows.TryGetValue key with
                            | true, row ->
                                Some
                                    {
                                        Key = row.Key
                                        Version = row.Version
                                        Payload = row.Payload
                                        ETag = Some row.Tag
                                    }
                            | _ -> None)
            }

        member _.SaveMany records =
            let keys = HashSet<string>(StringComparer.Ordinal)

            for record in records do
                validateText "records" record.Key
                validateText "records" record.Payload

                if not (keys.Add record.Key) then
                    invalidArg (nameof records) "A batch must contain distinct keys."

                match record.ETag with
                | Some tag ->
                    let mutable parsed = Guid.Empty

                    if
                        not (Guid.TryParseExact (tag, "D", &parsed))
                        || parsed = Guid.Empty
                    then
                        invalidArg (nameof records) "An ETag must be a non-empty GUID in D format."
                | None -> ()

            if records.IsEmpty then
                Task.CompletedTask
            else
                mutate (fun rows ->
                    for record in records do
                        match record.ETag with
                        | Some tag ->
                            match rows.TryGetValue record.Key with
                            | true, row when row.Tag = tag -> ()
                            | _ -> raise (KurrentDBStateConflictException record.Key)
                        | None -> ()

                    for record in records do
                        rows[record.Key] <-
                            {
                                Key = record.Key
                                Version = record.Version
                                Payload = record.Payload
                                Tag = Guid.NewGuid().ToString("D")
                            })

        member _.DeleteMany keys =
            for key in keys do
                validateText "keys" key

            if keys.IsEmpty then
                Task.CompletedTask
            else
                mutate (fun rows ->
                    for key in keys do
                        rows.Remove key |> ignore)

        member _.Clear() =
            mutate (fun rows -> rows.Clear ())
