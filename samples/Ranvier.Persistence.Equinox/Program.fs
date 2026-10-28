module Ranvier.Samples.EquinoxFsCodec.Program

open System
open EventStore.Client
open Ranvier
open Ranvier.Persistence

let private codec: JournalCodec<int> =
    {
        Name = "int"
        Version = 1
        Encode = string
        Decode =
            fun text ->
                match Int32.TryParse text with
                | true, value -> Ok value
                | _ -> Error "Expected an integer"
    }

let private countKey = Persist.key "count" (PersistCodec.ofJournal codec)

let private writeCount session =
    use graph = new Graph ()
    use active = graph.Activate ()
    let count = Persist.signal session countKey 0
    count.Value <- 21
    session.FlushAsync ()

let private readCount session =
    use graph = new Graph ()
    use active = graph.Activate ()
    let count = Persist.signal session countKey 0
    let doubled = createMemo (fun _ -> count.Value * 2)
    count.Value, doubled.Value

/// <summary>Saves count=21, reloads persisted inputs into a fresh graph, and returns count and its doubled memo.</summary>
let roundTrip (store: IStateStore) =
    task {
        let! session = Persist.loadAsync store [ countKey ]
        do! writeCount session
        let! restored = Persist.loadAsync store [ countKey ]
        return readCount restored
    }

/// <summary>Runs the round trip using RANVIER_EQUINOX_CONNECTION and an optional scope argument.</summary>
[<EntryPoint>]
let main args =
    try
        let connection = Environment.GetEnvironmentVariable "RANVIER_EQUINOX_CONNECTION"

        if String.IsNullOrWhiteSpace connection then
            invalidOp "Set RANVIER_EQUINOX_CONNECTION to an EventStoreDB or KurrentDB gRPC connection string."

        let scope = if args.Length = 0 then "round-trip" else args[0]
        use client = new EventStoreClient (EventStoreClientSettings.Create connection)
        let store = EquinoxStateStore (client, scope) :> IStateStore
        let count, doubled = roundTrip(store).GetAwaiter().GetResult()
        printfn "Restored count=%d; doubled=%d" count doubled
        if (count, doubled) = (21, 42) then 0 else 1
    with error ->
        eprintfn "%s" error.Message
        1
