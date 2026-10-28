module Ranvier.Persistence.Tests.Coalescing

open System
open Expecto
open Ranvier
open Ranvier.Persistence
open Ranvier.Persistence.Tests.Support

let private keys =
    [
        for name in [ "a"; "b"; "c" ] -> Persist.key name (PersistCodec.ofJournal (intCodec 1))
    ]

let private declared = keys |> List.map (fun key -> key :> IPersistKey)

let private sessionOver (store: RecordingStore) =
    Persist.loadAsync store declared
    |> Async.AwaitTask

/// <summary>
/// Builds a graph with <c>options</c>, makes the three persisted signals, runs <c>write</c> on them, then waits for
/// every save.
/// </summary>
let private runWith (options: GraphOptions) (store: RecordingStore) (write: Graph -> Signal<int> list -> unit) =
    async {
        let! session = sessionOver store

        do
            use graph = new Graph (options)
            use _active = graph.Activate ()

            let signals =
                keys
                |> List.map (fun key -> Persist.signal session key 0)

            write graph signals

        do! session.FlushAsync () |> Async.AwaitTask
    }

let private batchOfThree _ (signals: Signal<int> list) =
    batch (fun () ->
        signals
        |> List.iteri (fun index signal -> signal.Value <- index + 10))

let private sameKeyTwice _ (signals: Signal<int> list) =
    batch (fun () ->
        signals[0].Value <- 1
        signals[1].Value <- 2
        signals[0].Value <- 3)

let private dispatcherCases (name: string) (options: unit -> GraphOptions) =
    testList
        name
        [
            testCaseAsync "a batch writing three persisted signals saves once with three records"
            <| async {
                let store = RecordingStore ()
                do! runWith (options ()) store batchOfThree
                Expect.equal (savedPayloads store) [ [ "a", "10"; "b", "11"; "c", "12" ] ] "one save, three records"
            }

            testCaseAsync "a key written twice in one batch is saved once with its last value"
            <| async {
                let store = RecordingStore ()
                do! runWith (options ()) store sameKeyTwice
                Expect.equal (savedPayloads store) [ [ "a", "3"; "b", "2" ] ] "one save, the last value of a"
            }

            testCaseAsync "unbatched writes save once each"
            <| async {
                let store = RecordingStore ()

                do!
                    runWith (options ()) store (fun _ signals ->
                        signals[0].Value <- 1
                        signals[2].Value <- 2)

                Expect.equal (savedPayloads store) [ [ "a", "1" ]; [ "c", "2" ] ] "a save per flush"
            }
        ]

#if !FABLE_COMPILER
/// <summary>A synchronisation context that queues its posts until <c>Run</c>.</summary>
type private QueueContext() =
    inherit Threading.SynchronizationContext()
    let queue = Collections.Concurrent.ConcurrentQueue<unit -> unit>()

    override _.Post(callback, state) =
        queue.Enqueue (fun () -> callback.Invoke state)

    member _.Run() =
        let mutable work = Unchecked.defaultof<unit -> unit>

        while queue.TryDequeue &work do
            work ()

let private synchronizationContextCases =
    testList
        "SynchronizationContextDispatcher"
        [
            testCaseAsync "a batch dispatched from another thread saves once with three records"
            <| async {
                let store = RecordingStore ()
                let context = QueueContext ()

                let options =
                    GraphOptions.Default.WithDispatcher (SynchronizationContextDispatcher context)

                do!
                    runWith options store (fun graph signals ->
                        let writer =
                            Threading.Thread (fun () -> graph.Dispatch (fun () -> batchOfThree graph signals))

                        writer.Start ()
                        writer.Join ()

                        Expect.isEmpty store.Saves "nothing saves before the context runs the drain"
                        context.Run ())

                Expect.equal (savedPayloads store) [ [ "a", "10"; "b", "11"; "c", "12" ] ] "one save, three records"
            }

            testCaseAsync "a batch on the owning thread saves once with three records"
            <| async {
                let store = RecordingStore ()

                let options =
                    GraphOptions.Default.WithDispatcher (SynchronizationContextDispatcher (QueueContext ()))

                do! runWith options store batchOfThree
                Expect.equal (savedPayloads store) [ [ "a", "10"; "b", "11"; "c", "12" ] ] "one save, three records"
            }

            testCaseAsync "a key written twice in one batch is saved once with its last value"
            <| async {
                let store = RecordingStore ()

                let options =
                    GraphOptions.Default.WithDispatcher (SynchronizationContextDispatcher (QueueContext ()))

                do! runWith options store sameKeyTwice
                Expect.equal (savedPayloads store) [ [ "a", "3"; "b", "2" ] ] "one save, the last value of a"
            }
        ]
#endif

[<Tests>]
let tests =
    testList
        "Coalescing"
        [
            testCaseAsync "the first read returns the stored value and creating the signal saves nothing"
            <| async {
                let store = RecordingStore ()

                do!
                    (store :> IStateStore).SaveMany [ record "a" 1 "41" ]
                    |> Async.AwaitTask

                let seen = ResizeArray<int>()
                let! session = sessionOver store
                let before = store.Saves.Length

                do
                    use graph = new Graph ()
                    use _active = graph.Activate ()
                    let signal = Persist.signal session keys[0] 0
                    createEffect (fun () -> seen.Add signal.Value)
                    Expect.equal signal.Value 41 "the first read is the stored value"

                do! session.FlushAsync () |> Async.AwaitTask
                Expect.equal (List.ofSeq seen) [ 41 ] "no effect observes the fallback"
                Expect.equal store.Saves.Length before "nothing is saved"
            }

            testCaseAsync "a signal created and written in one batch saves the write"
            <| async {
                let store = RecordingStore ()
                let! session = sessionOver store

                do
                    use graph = new Graph ()
                    use _active = graph.Activate ()

                    batch (fun () ->
                        let signal = Persist.signal session keys[0] 0
                        signal.Value <- 5)

                do! session.FlushAsync () |> Async.AwaitTask
                Expect.equal (savedPayloads store) [ [ "a", "5" ] ] "the write saves"
            }

            testCaseAsync "a second live signal of one key is rejected"
            <| async {
                let! session = sessionOver (RecordingStore ())
                use graph = new Graph ()
                use _active = graph.Activate ()
                Persist.signal session keys[0] 0 |> ignore

                Expect.throwsT<InvalidOperationException> (fun () -> Persist.signal session keys[0] 0 |> ignore) "one live signal per key"
            }

            testCaseAsync "a key is free again once its signal's owner is disposed"
            <| async {
                let! session = sessionOver (RecordingStore ())
                use graph = new Graph ()
                use _active = graph.Activate ()

                let owner =
                    createRoot (fun owner ->
                        Persist.signal session keys[0] 0 |> ignore
                        owner)

                owner.Dispose ()
                Persist.signal session keys[0] 0 |> ignore
            }

            testCaseAsync "a signal re-created for a key starts from its last written value"
            <| async {
                let store = RecordingStore ()

                do!
                    (store :> IStateStore).SaveMany [ record "a" 1 "41" ]
                    |> Async.AwaitTask

                let! session = sessionOver store
                let before = store.Saves.Length
                let seen = ResizeArray<int>()

                do
                    use graph = new Graph ()
                    use _active = graph.Activate ()

                    let mount () =
                        createRoot (fun owner -> owner, Persist.signal session keys[0] 0)

                    let first, signal = mount ()
                    signal.Value <- 5
                    first.Dispose ()

                    let second, signal = mount ()
                    createEffect (fun () -> seen.Add signal.Value)
                    Expect.equal signal.Value 5 "the re-created signal reads the last written value"
                    second.Dispose ()

                do! session.FlushAsync () |> Async.AwaitTask
                Expect.equal (List.ofSeq seen) [ 5 ] "no effect observes the loaded value"
                Expect.equal (savedPayloads store |> List.skip before) [ [ "a", "5" ] ] "re-creating the signal saves nothing"
            }

            testCaseAsync "a write in the batch that disposes its signal's owner is saved and restored"
            <| async {
                let store = RecordingStore ()

                do!
                    (store :> IStateStore).SaveMany [ record "a" 1 "41" ]
                    |> Async.AwaitTask

                let! session = sessionOver store
                let before = store.Saves.Length

                do
                    use graph = new Graph ()
                    use _active = graph.Activate ()

                    let mount () =
                        createRoot (fun owner -> owner, Persist.signal session keys[0] 0)

                    let first, signal = mount ()

                    batch (fun () ->
                        signal.Value <- 5
                        first.Dispose ())

                    let second, signal = mount ()
                    Expect.equal signal.Value 5 "the re-created signal reads the write"
                    second.Dispose ()

                do! session.FlushAsync () |> Async.AwaitTask
                Expect.equal (savedPayloads store |> List.skip before) [ [ "a", "5" ] ] "the write saves once"
            }

            testCaseAsync "a session serves one graph"
            <| async {
                let! session = sessionOver (RecordingStore ())

                do
                    use first = new Graph ()
                    use _active = first.Activate ()
                    Persist.signal session keys[0] 0 |> ignore

                use second = new Graph ()
                use _active = second.Activate ()

                Expect.throwsT<InvalidOperationException> (fun () -> Persist.signal session keys[1] 0 |> ignore) "a second graph is rejected"
            }

            dispatcherCases "ManualDispatcher" (fun () -> GraphOptions.Default.WithDispatcher (ManualDispatcher ()))
            dispatcherCases "ImmediateDispatcher" (fun () -> GraphOptions.Default.WithDispatcher (ImmediateDispatcher ()))
            dispatcherCases "default dispatcher" (fun () -> GraphOptions.Default)
#if !FABLE_COMPILER
            synchronizationContextCases
#endif
        ]
