module Ranvier.Persistence.AotSmoke.PersistSmoke

open System
open System.IO
open Ranvier
open Ranvier.Persistence
open Ranvier.Wire
open Ranvier.Wire.Thoth.Json.Core

type private Note = { Title: string; Count: int }

type private Mark =
    | Star of level: int
    | Plain

let private note: Codec<Note> =
    objectCodec {
        let! title = Codec.field "title" _.Title Codec.string
        and! count = Codec.field "count" _.Count Codec.int
        return { Title = title; Count = count }
    }

let private mark: Codec<Mark> =
    variantCodec {
        let! star = Codec.case "star" Star Codec.int
        and! plain = Codec.case "plain" (fun () -> Plain) Codec.unit

        return
            function
            | Star level -> star level
            | Plain -> plain ()
    }

let private wireKind (error: WireError) =
    match error with
    | WireError.Syntax _ -> "Syntax"
    | WireError.Limit _ -> "Limit"
    | WireError.Shape _ -> "Shape"

let private persistCodec (name: string) (codec: Codec<'T>) : PersistCodec<'T> =
    let wire = WireCodec.ofCodec codec

    PersistCodec.ofJournal
        {
            Name = name
            Version = 1
            Encode = wire.Encode
            Decode = wire.Decode >> Result.mapError wireKind
        }

let private noteKey = Persist.key "note" (persistCodec "note" note)
let private markKey = Persist.key "mark" (persistCodec "mark" mark)
let private declared: IPersistKey list = [ noteKey; markKey ]

/// <summary>
/// The persisted values a fresh graph reads after a graph writes them in one batch and flushes them to a
/// <c>FileStateStore</c>, as text.
/// </summary>
let private roundTrip (directory: string) =
    let first = (Persist.loadAsync (FileStateStore directory) declared).Result

    do
        use graph = new Graph ()
        use _active = graph.Activate ()
        let note = Persist.signal first noteKey { Title = ""; Count = 0 }
        let mark = Persist.signal first markKey Plain

        batch (fun () ->
            note.Value <- { Title = "groceries"; Count = 2 }
            mark.Value <- Star 3)

    first.FlushAsync().Wait()
    let second = (Persist.loadAsync (FileStateStore directory) declared).Result
    use graph = new Graph ()
    use _active = graph.Activate ()
    let note = (Persist.signal second noteKey { Title = ""; Count = 0 }).Value
    let mark = (Persist.signal second markKey Plain).Value

    let markText =
        match mark with
        | Star level -> "star " + string level
        | Plain -> "plain"

    note.Title
    + ":"
    + string note.Count
    + ";"
    + markText

/// <summary>The message of the load failure for a stored record newer than the codec.</summary>
let private newerMessage () =
    let store = MemoryStateStore () :> IStateStore

    store
        .SaveMany(
            [
                {
                    Key = "note"
                    Version = 2
                    Payload = "{}"
                    ETag = None
                }
            ]
        )
        .Wait()

    try
        (Persist.loadAsync store [ noteKey ]).Result
        |> ignore

        "loaded"
    with :? AggregateException as error ->
        match error.InnerException with
        | :? PersistLoadException as failure -> failure.Message
        | other -> other.GetType().Name

/// <summary>Each persistence check as its name, whether it passed, and the text it observed.</summary>
let checks () : (string * bool * string) list =
    let directory =
        Path.Combine (
            Path.GetTempPath (),
            "ranvier-aot-persist-"
            + Guid.NewGuid().ToString "N"
        )

    try
        let restored = roundTrip directory
        let newer = newerMessage ()

        let expectedNewer =
            "Persisted key \"note\" cannot be restored: stored version 2 is newer than the codec's version 1"

        [
            "persisted signals round trip through a file store", restored = "groceries:2;star 3", restored
            "a newer stored version fails the load naming the key", newer = expectedNewer, newer
        ]
    finally
        if Directory.Exists directory then
            Directory.Delete (directory, true)
