module Ranvier.Persistence.Tests.Tracking

open Expecto
open Ranvier
open Ranvier.Elmish
open Ranvier.Persistence
open Ranvier.Persistence.Tests.Support

type private Model = { Count: int; Step: int }

type private Msg =
    | Increment
    | SetStep of int

let private update msg model =
    match msg with
    | Increment ->
        { model with
            Count = model.Count + model.Step
        }
    | SetStep step -> { model with Step = step }

let private modelCodec: PersistCodec<Model> =
    PersistCodec.ofJournal
        {
            Name = "model"
            Version = 1
            Encode = fun model -> string model.Count + "," + string model.Step
            Decode =
                fun payload ->
                    match payload.Split ',' with
                    | [| count; step |] -> Result.Ok { Count = int count; Step = int step }
                    | _ -> Result.Error ("not a model: " + payload)
        }

let private modelKey = Persist.key "app/model" modelCodec
let private init = { Count = 0; Step = 1 }

/// <summary>
/// Loads the model key from <c>store</c>, makes an <c>Mvu</c> restored and tracked through the session, dispatches
/// <c>messages</c>, then waits for every save. Returns the model after the messages.
/// </summary>
let private runMvu (store: IStateStore) (messages: Msg list) =
    async {
        let! session =
            Persist.loadAsync store [ modelKey ]
            |> Async.AwaitTask

        let model =
            use graph = new Graph (GraphOptions.Default)
            use _active = graph.Activate ()
            let mvu = Mvu.create (Persist.restore session modelKey init) update
            Persist.track session modelKey (fun () -> mvu.Model)

            for msg in messages do
                mvu.Dispatch msg

            untrack (fun () -> mvu.Model)

        do! session.FlushAsync () |> Async.AwaitTask
        return model
    }

[<Tests>]
let tests =
    testList
        "Tracking"
        [
            testCaseAsync "an Mvu model restored and tracked survives a reload"
            <| async {
                let store = MemoryStateStore () :> IStateStore
                let! first = runMvu store [ SetStep 5; Increment; Increment ]
                Expect.equal first { Count = 10; Step = 5 } "the model after the messages"
                let! reloaded = runMvu store [ Increment ]
                Expect.equal reloaded { Count = 15; Step = 5 } "the reload starts from the stored model"
            }

            testCaseAsync "each model change saves the model record"
            <| async {
                let store = RecordingStore ()
                let! _ = runMvu store [ SetStep 2; Increment ]
                Expect.equal (savedPayloads store) [ [ "app/model", "0,2" ]; [ "app/model", "2,2" ] ] "a save per dispatch"
            }

            testCaseAsync "tracking the restored model saves nothing until it changes"
            <| async {
                let inner = MemoryStateStore () :> IStateStore
                let! _ = runMvu inner [ Increment ]
                let store = RecordingStore inner
                let! model = runMvu store []
                Expect.equal model { Count = 1; Step = 1 } "the stored model"
                Expect.isEmpty store.Saves "no save"
            }

            testCaseAsync "tracking a fallback model saves nothing until it changes"
            <| async {
                let store = RecordingStore ()
                let! model = runMvu store []
                Expect.equal model init "the fallback model"
                Expect.isEmpty store.Saves "no save"
            }

            testCaseAsync "a tracked read of another value saves through the key's codec"
            <| async {
                let store = RecordingStore ()

                let! session =
                    Persist.loadAsync store [ modelKey ]
                    |> Async.AwaitTask

                do
                    use graph = new Graph (GraphOptions.Default)
                    use _active = graph.Activate ()
                    let count = createSignal 0
                    Persist.track session modelKey (fun () -> { init with Count = count.Value })
                    count.Value <- 4

                do! session.FlushAsync () |> Async.AwaitTask
                Expect.equal (savedPayloads store) [ [ "app/model", "4,1" ] ] "the read value"
                Expect.equal (Persist.restore session modelKey init) { init with Count = 4 } "the session remembers it"
            }

            testCaseAsync "a read tracked and changed in one batch saves the change"
            <| async {
                let store = RecordingStore ()

                let! session =
                    Persist.loadAsync store [ modelKey ]
                    |> Async.AwaitTask

                do
                    use graph = new Graph (GraphOptions.Default)
                    use _active = graph.Activate ()
                    let count = createSignal 0

                    batch (fun () ->
                        Persist.track session modelKey (fun () -> { init with Count = count.Value })
                        count.Value <- 5)

                do! session.FlushAsync () |> Async.AwaitTask
                Expect.equal (savedPayloads store) [ [ "app/model", "5,1" ] ] "the changed value"
            }
        ]
