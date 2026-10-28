module Ranvier.Persistence.Tests.RoundTrip

open Expecto
open Ranvier
open Ranvier.Persistence
open Ranvier.Persistence.Tests.Support

let private textCodec: JournalCodec<string> =
    {
        Name = "text"
        Version = 1
        Encode = id
        Decode = Result.Ok
    }

let private title = Persist.key "title" (PersistCodec.ofJournal textCodec)
let private count = Persist.key "count" (PersistCodec.ofJournal (intCodec 1))
let private declared: IPersistKey list = [ title; count ]

/// <summary>The inputs and derived values of one run of the application.</summary>
type private Run =
    {
        Title: Signal<string>
        Count: Signal<int>
        Label: Memo<string>
        Doubled: Memo<int>
    }

/// <summary>The application: two persisted inputs and two memos over them, made in the current graph.</summary>
let private build (session: PersistSession) =
    let title = Persist.signal session title "untitled"
    let count = Persist.signal session count 0

    {
        Title = title
        Count = count
        Label = createMemo (fun _ -> title.Value + ":" + string count.Value)
        Doubled = createMemo (fun _ -> count.Value * 2)
    }

let private load directory =
    Persist.loadAsync (FileStateStore directory) declared
    |> Async.AwaitTask

[<Tests>]
let tests =
    testList
        "RoundTrip"
        [
            testCaseAsync "inputs saved to a file store restore into a fresh graph with equal derived values"
            <| async {
                let directory = FileStore.freshDirectory ()
                let! first = load directory

                let uninterrupted =
                    use graph = new Graph ()
                    use _active = graph.Activate ()
                    let run = build first

                    batch (fun () ->
                        run.Title.Value <- "groceries"
                        run.Count.Value <- 2)

                    run.Count.Value <- 3
                    run.Title.Value, run.Count.Value, run.Label.Value, run.Doubled.Value

                do! first.FlushAsync () |> Async.AwaitTask

                let! second = load directory
                let observed = ResizeArray<string * int>()

                let restored =
                    use graph = new Graph ()
                    use _active = graph.Activate ()
                    let run = build second
                    createEffect (fun () -> observed.Add (run.Title.Value, run.Count.Value))
                    run.Title.Value, run.Count.Value, run.Label.Value, run.Doubled.Value

                Expect.equal restored uninterrupted "inputs and memos equal the uninterrupted run"
                Expect.equal (List.ofSeq observed) [ "groceries", 3 ] "the effect observes the stored values only"
            }
        ]
