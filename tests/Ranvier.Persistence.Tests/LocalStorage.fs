module Ranvier.Persistence.Tests.LocalStorage

#if FABLE_COMPILER
open System.Collections.Generic
open Expecto
open Ranvier
open Ranvier.Wire
open Ranvier.Wire.Thoth.Json.Core
open Ranvier.Persistence
open Ranvier.Persistence.Tests.Support

/// <summary>Web Storage in memory, for Node.js, which has no <c>localStorage</c>. Keys keep insertion order.</summary>
type MemoryWebStorage() =
    let keys = ResizeArray<string>()
    let items = Dictionary<string, string>()

    member _.Items = [ for key in keys -> key, items[key] ]

    interface IWebStorage with
        member _.length = keys.Count

        member _.key index =
            if index < keys.Count then keys[index] else null

        member _.getItem key =
            match items.TryGetValue key with
            | true, item -> item
            | _ -> null

        member _.setItem(key, value) =
            if not (items.ContainsKey key) then
                keys.Add key

            items[key] <- value

        member _.removeItem key =
            if items.Remove key then
                keys.Remove key |> ignore

type private Point = { X: int; Label: string }

type private Shape =
    | Circle of radius: int
    | Square of side: int * name: string
    | Dot

let private point: Codec<Point> =
    objectCodec {
        let! x = Codec.field "x" _.X Codec.int
        and! label = Codec.field "label" _.Label Codec.string
        return { X = x; Label = label }
    }

let private shape: Codec<Shape> =
    variantCodec {
        let! circle = Codec.case "circle" Circle Codec.int
        and! square = Codec.case "square" Square (Codec.tuple2 Codec.int Codec.string)
        and! dot = Codec.case "dot" (fun () -> Dot) Codec.unit

        return
            function
            | Circle radius -> circle radius
            | Square (side, name) -> square (side, name)
            | Dot -> dot ()
    }

/// <summary>A persist codec over a Wire codec, as the journal guide adapts one.</summary>
let private persistCodec (name: string) (codec: Codec<'T>) : PersistCodec<'T> =
    let wire = WireCodec.ofCodec codec

    PersistCodec.ofJournal
        {
            Name = name
            Version = 1
            Encode = wire.Encode
            Decode = wire.Decode >> Result.mapError string
        }

let private pointKey = Persist.key "point" (persistCodec "point" point)
let private shapeKey = Persist.key "shape" (persistCodec "shape" shape)
let private declared: IPersistKey list = [ pointKey; shapeKey ]

let private storeOver (storage: MemoryWebStorage) =
    LocalStorageStateStore ("app/", storage) :> IStateStore

[<Tests>]
let tests =
    testList
        "LocalStorageStateStore"
        [
            StoreConformance.suite "conformance" (fun () -> storeOver (MemoryWebStorage ()))

            testCaseAsync "a record and a union round trip through Wire codecs"
            <| async {
                let storage = MemoryWebStorage ()

                let! session =
                    Persist.loadAsync (storeOver storage) declared
                    |> Async.AwaitTask

                do
                    use graph = new Graph (GraphOptions.Default)
                    use _active = graph.Activate ()
                    let point = Persist.signal session pointKey { X = 0; Label = "" }
                    let shape = Persist.signal session shapeKey Dot

                    batch (fun () ->
                        point.Value <- { X = 3; Label = "três \"quoted\"" }
                        shape.Value <- Square (4, "big"))

                do! session.FlushAsync () |> Async.AwaitTask

                let! reloaded =
                    Persist.loadAsync (storeOver storage) declared
                    |> Async.AwaitTask

                let restored =
                    use graph = new Graph (GraphOptions.Default)
                    use _active = graph.Activate ()
                    let point = (Persist.signal reloaded pointKey { X = 0; Label = "" }).Value
                    let shape = (Persist.signal reloaded shapeKey Dot).Value
                    point, shape

                Expect.equal restored ({ X = 3; Label = "três \"quoted\"" }, Square (4, "big")) "the stored values"
            }

            testCaseAsync "items live under the prefix as the version and the payload"
            <| async {
                let storage = MemoryWebStorage ()

                do!
                    (storeOver storage).SaveMany [ record "a" 2 "x:y" ]
                    |> Async.AwaitTask

                Expect.equal storage.Items [ "app/a", "2:x:y" ] "one item under the prefix"

                let! loaded =
                    (storeOver storage).LoadMany [ "a" ]
                    |> Async.AwaitTask

                Expect.equal loaded [ record "a" 2 "x:y" ] "the record loads back"
            }

            testCaseAsync "a clear removes the items under the prefix only"
            <| async {
                let storage = MemoryWebStorage ()
                let other = LocalStorageStateStore ("other/", storage) :> IStateStore
                (storage :> IWebStorage).setItem("unrelated", "kept")

                do!
                    (storeOver storage).SaveMany [ record "a" 1 "1"; record "b" 1 "2" ]
                    |> Async.AwaitTask

                do!
                    other.SaveMany [ record "a" 1 "3" ]
                    |> Async.AwaitTask

                do! (storeOver storage).Clear() |> Async.AwaitTask
                Expect.equal storage.Items [ "unrelated", "kept"; "other/a", "1:3" ] "the other items stay"
            }

            testCaseAsync "an item that is not a record fails the load naming the key"
            <| async {
                let storage = MemoryWebStorage ()
                (storage :> IWebStorage).setItem("app/a", "garbage")

                let! error =
                    failureOf (
                        (storeOver storage).LoadMany [ "a" ]
                        |> Async.AwaitTask
                    )

                Expect.stringContains error.Message "\"a\"" "the message names the key"
            }
        ]
#endif
