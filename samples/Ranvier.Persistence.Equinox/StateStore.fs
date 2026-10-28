namespace Ranvier.Samples.EquinoxFsCodec

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json
open System.Threading.Tasks
open EventStore.Client
open Ranvier.Persistence

/// <summary>The versioned event contract stored in this sample's streams.</summary>
module Events =
    /// <summary>A persisted input with an independent GUID concurrency token.</summary>
    type Row =
        {
            Key: string
            /// <summary>The payload codec version, independent of the stream revision.</summary>
            Version: int
            Payload: string
            /// <summary>The record's opaque GUID concurrency token in D format.</summary>
            Tag: string
        }

    /// <summary>The complete current records of an application scope.</summary>
    type Snapshot = { Records: Row array }

    /// <summary>The snapshot formats recognized by the sample.</summary>
    type Event =
        /// <summary>Replaces all current records with the version-one snapshot.</summary>
        | SnapshotV1 of Snapshot

        interface TypeShape.UnionContract.IUnionContract

module private Model =
    open Events

    let validate (snapshot: Snapshot) =
        if isNull (box snapshot) || isNull snapshot.Records then
            raise (InvalidDataException ("Snapshot records are required."))

        let keys = HashSet<string>(StringComparer.Ordinal)
        let utf8 = UTF8Encoding (false, true)

        for row in snapshot.Records do
            let mutable tag = Guid.Empty

            if
                isNull (box row)
                || isNull row.Key
                || isNull row.Payload
                || not (Guid.TryParseExact (row.Tag, "D", &tag))
                || tag = Guid.Empty
                || not (keys.Add row.Key)
            then
                raise (InvalidDataException ("The snapshot contains an invalid record."))

            utf8.GetByteCount row.Key |> ignore
            utf8.GetByteCount row.Payload |> ignore

        snapshot

    let private rawCodec = FsCodec.SystemTextJson.Codec.Create<Event>()

    let codec =
        { new FsCodec.IEventCodec<Event, ReadOnlyMemory<byte>, unit> with
            member _.Encode(context, event) =
                let encoded = rawCodec.Encode (context, event)

                if encoded.Data.Length > 1024 * 1024 then
                    invalidOp "The encoded scope exceeds the sample's 1 MiB snapshot limit."

                encoded

            member _.Decode encoded =
                try
                    match rawCodec.Decode encoded with
                    | ValueSome (SnapshotV1 snapshot as event) ->
                        validate snapshot |> ignore
                        ValueSome event
                    | ValueNone -> raise (InvalidDataException ("Unknown snapshot event type."))
                with
                | :? JsonException as error -> raise (InvalidDataException ("Invalid snapshot JSON.", error))
                | :? ArgumentException as error -> raise (InvalidDataException ("Invalid snapshot data.", error))
        }

    let fold (state: Map<string, Row>) events =
        (state, events)
        ||> Array.fold (fun _ (SnapshotV1 snapshot) ->
            snapshot.Records
            |> Array.map (fun row -> row.Key, row)
            |> Map.ofArray)

/// <summary>A conditional save whose key is absent or has a different stored ETag.</summary>
type EquinoxStateConflictException(key: string) =
    inherit
        InvalidOperationException(
            "The stored version of key '"
            + key
            + "' has changed."
        )

    /// <summary>The key whose conditional save was rejected.</summary>
    member _.Key = key

/// <summary>An Equinox/FsCodec sample store for small scoped snapshots in EventStoreDB or KurrentDB.</summary>
/// <remarks>Mutations append the full scope, limited to 1 MiB, with at most 32 append attempts.
/// Explicit record ETags are rechecked after stream conflicts. Delete and clear retain historical events.</remarks>
type EquinoxStateStore
    /// <summary>Creates an uncached Decider for an adapter-owned scoped stream.</summary>
    /// <param name="client">Caller-owned gRPC client, shared across operations.</param>
    /// <param name="scope">Non-blank valid Unicode, limited to 512 UTF-8 bytes.</param>
    (client: EventStoreClient, scope: string) =
    let utf8 = UTF8Encoding (false, true)

    let streamId =
        if isNull client then
            nullArg (nameof client)

        if String.IsNullOrWhiteSpace scope then
            invalidArg (nameof scope) "Scope must be non-blank."

        let bytes = utf8.GetBytes scope

        if bytes.Length > 512 then
            invalidArg (nameof scope) "Scope must fit in 512 UTF-8 bytes."

        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    let category =
        Equinox.EventStoreDb.EventStoreCategory (
            Equinox.EventStoreDb.EventStoreContext (Equinox.EventStoreDb.EventStoreConnection (client)),
            "ranvierEquinoxV1",
            Model.codec,
            Func<_, _, _>(Model.fold),
            Map.empty,
            Equinox.EventStoreDb.AccessStrategy.LatestKnownEvent,
            Equinox.CachingStrategy.NoCaching
        )

    let decider =
        Equinox.Decider.forStream Serilog.Log.Logger category (FsCodec.StreamId.gen id streamId)

    let validateText name (value: string) =
        if isNull value then
            nullArg name

        utf8.GetByteCount value |> ignore

    let transact (apply: Map<string, Events.Row> -> Map<string, Events.Row>) =
        decider.Transact (
            (fun state ->
                let next = apply state

                [|
                    Events.SnapshotV1
                        {
                            Records = next |> Map.toArray |> Array.map snd
                        }
                |]),
            attempts = Equinox.Attempts.Max 32
        )
        |> Async.StartAsTask
        :> Task

    interface IStateStore with
        member _.LoadMany keys =
            for key in keys do
                validateText "keys" key

            if keys.IsEmpty then
                Task.FromResult []
            else
                decider.Query (fun rows ->
                    keys
                    |> List.distinct
                    |> List.choose (fun key ->
                        rows
                        |> Map.tryFind key
                        |> Option.map (fun row ->
                            {
                                Key = row.Key
                                Version = row.Version
                                Payload = row.Payload
                                ETag = Some row.Tag
                            })))
                |> Async.StartAsTask

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

            let nextRows =
                records
                |> List.map (fun row ->
                    ({
                        Key = row.Key
                        Version = row.Version
                        Payload = row.Payload
                        Tag = Guid.NewGuid().ToString("D")
                    }
                    : Events.Row))

            if records.IsEmpty then
                Task.CompletedTask
            else
                transact (fun state ->
                    for record in records do
                        match record.ETag with
                        | Some tag ->
                            match Map.tryFind record.Key state with
                            | Some row when row.Tag = tag -> ()
                            | _ -> raise (EquinoxStateConflictException record.Key)
                        | None -> ()

                    (state, nextRows)
                    ||> List.fold (fun current row -> Map.add row.Key row current))

        member _.DeleteMany keys =
            for key in keys do
                validateText "keys" key

            if keys.IsEmpty then
                Task.CompletedTask
            else
                transact (fun state ->
                    (state, keys)
                    ||> List.fold (fun rows key -> Map.remove key rows))

        member _.Clear() =
            transact (fun _ -> Map.empty)
