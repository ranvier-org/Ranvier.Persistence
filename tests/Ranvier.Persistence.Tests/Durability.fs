module Ranvier.Persistence.Tests.Durability

open System
open System.Threading.Tasks
open Expecto
open Ranvier
open Ranvier.Persistence
open Ranvier.Persistence.Tests.Support

let private a = Persist.key "a" (PersistCodec.ofJournal (intCodec 1))
let private b = Persist.key "b" (PersistCodec.ofJournal (intCodec 1))
let private declared: IPersistKey list = [ a; b ]

let private withBuster buster =
    { PersistOptions.defaults with
        Buster = buster
    }

/// <summary>Loads <c>a</c> and <c>b</c>, runs <c>write</c> on their persisted signals, then waits for every save.</summary>
let private session (options: PersistOptions) (store: IStateStore) (write: Signal<int> -> Signal<int> -> unit) =
    async {
        let! session =
            Persist.loadWith options store declared
            |> Async.AwaitTask

        do
            use graph = new Graph (GraphOptions.Default.WithDispatcher (ImmediateDispatcher ()))
            use _active = graph.Activate ()
            write (Persist.signal session a 0) (Persist.signal session b 0)

        do! session.FlushAsync () |> Async.AwaitTask
        return session
    }

let private restored (options: PersistOptions) (store: IStateStore) =
    async {
        let! session =
            Persist.loadWith options store declared
            |> Async.AwaitTask

        return Persist.restore session a 0, Persist.restore session b 0
    }

let private storeWith (records: StoredRecord list) =
    let store = MemoryStateStore ()
    (store :> IStateStore).SaveMany records |> ignore
    store :> IStateStore

/// <summary>
/// A memory store whose <c>SaveMany</c> calls fault with the messages of <c>faults</c>, and whose <c>DeleteMany</c>
/// and <c>Clear</c> calls fault with the messages of <c>deleteFaults</c>, in call order.
/// </summary>
type private FaultingStore(faults: string option list, deleteFaults: string option list) =
    let inner = MemoryStateStore () :> IStateStore
    let mutable remaining = faults
    let mutable remainingDeletes = deleteFaults

    new(faults) = FaultingStore (faults, [])

    member _.Inner = inner

    interface IStateStore with
        member _.LoadMany keys =
            inner.LoadMany keys

        member _.SaveMany records =
            match remaining with
            | Some message :: rest ->
                remaining <- rest
                failed message
            | None :: rest ->
                remaining <- rest
                inner.SaveMany records
            | [] -> inner.SaveMany records

        member _.DeleteMany keys =
            match remainingDeletes with
            | Some message :: rest ->
                remainingDeletes <- rest
                failed message
            | None :: rest ->
                remainingDeletes <- rest
                inner.DeleteMany keys
            | [] -> inner.DeleteMany keys

        member _.Clear() =
            match remainingDeletes with
            | Some message :: rest ->
                remainingDeletes <- rest
                failed message
            | None :: rest ->
                remainingDeletes <- rest
                inner.Clear ()
            | [] -> inner.Clear ()

[<Tests>]
let tests =
    testList
        "Durability"
        [
            testCaseAsync "a buster mismatch keeps the unwritten records discarded on the next load"
            <| async {
                let store =
                    storeWith [ record "$ranvier/buster" 1 "v1"; record "a" 1 "1"; record "b" 1 "2" ]

                let! _ = session (withBuster (Some "v2")) store (fun a _ -> a.Value <- 5)
                let! values = restored (withBuster (Some "v2")) store
                Expect.equal values (5, 0) "only the record written after the bust is restored"
            }

            testCaseAsync "a buster mismatch discards the records of keys the busting load did not declare"
            <| async {
                let store =
                    storeWith [ record "$ranvier/buster" 1 "v1"; record "a" 1 "1"; record "b" 1 "2" ]

                let! busting =
                    Persist.loadWith (withBuster (Some "v2")) store [ a ]
                    |> Async.AwaitTask

                do
                    use graph = new Graph (GraphOptions.Default.WithDispatcher (ImmediateDispatcher ()))
                    use _active = graph.Activate ()
                    (Persist.signal busting a 0).Value <- 9

                do! busting.FlushAsync () |> Async.AwaitTask
                let! values = restored (withBuster (Some "v2")) store
                Expect.equal values (9, 0) "the undeclared stale record is discarded"
            }

            testCaseAsync "two sessions loaded against a stale buster keep each other's saves"
            <| async {
                let store =
                    storeWith [ record "$ranvier/buster" 1 "v1"; record "a" 1 "1"; record "b" 1 "2" ]

                let! first =
                    Persist.loadWith (withBuster (Some "v2")) store declared
                    |> Async.AwaitTask

                let! second =
                    Persist.loadWith (withBuster (Some "v2")) store declared
                    |> Async.AwaitTask

                do
                    use graph = new Graph (GraphOptions.Default.WithDispatcher (ImmediateDispatcher ()))
                    use _active = graph.Activate ()
                    (Persist.signal first a 0).Value <- 5

                do! first.FlushAsync () |> Async.AwaitTask

                do
                    use graph = new Graph (GraphOptions.Default.WithDispatcher (ImmediateDispatcher ()))
                    use _active = graph.Activate ()
                    (Persist.signal second b 0).Value <- 7

                do! second.FlushAsync () |> Async.AwaitTask
                let! values = restored (withBuster (Some "v2")) store
                Expect.equal values (5, 7) "the second session's bust keeps the first session's save"
            }

            testCaseAsync "a buster set on a store saved without one keeps the old records discarded on the next load"
            <| async {
                let store = storeWith [ record "a" 1 "1"; record "b" 1 "2" ]
                let! _ = session (withBuster (Some "v2")) store (fun a _ -> a.Value <- 5)
                let! values = restored (withBuster (Some "v2")) store
                Expect.equal values (5, 0) "only the record written after the bust is restored"
            }

            testCaseAsync "a buster removed from the options keeps the old records discarded on the next load"
            <| async {
                let store =
                    storeWith [ record "$ranvier/buster" 1 "v1"; record "a" 1 "1"; record "b" 1 "2" ]

                let! _ = session (withBuster None) store (fun a _ -> a.Value <- 5)
                let! values = restored (withBuster None) store
                Expect.equal values (5, 0) "only the record written after the bust is restored"
            }

            testCaseAsync "a buster mismatch removes the stored journal position"
            <| async {
                let store =
                    storeWith [ record "$ranvier/buster" 1 "v1"; record "$ranvier/position" 1 "7" ]

                let! _ = session (withBuster (Some "v2")) store (fun a _ -> a.Value <- 5)

                let! reloaded =
                    Persist.loadWith (withBuster (Some "v2")) store declared
                    |> Async.AwaitTask

                Expect.equal reloaded.Position None "the position of the stale session is removed"
            }

            testCaseAsync "FlushAsync faults with the first faulted save's exception, and later saves still run"
            <| async {
                let store = FaultingStore [ Some "disk full"; Some "second fault"; None ]

                let! error =
                    failureOf (
                        session PersistOptions.defaults store (fun a b ->
                            a.Value <- 1
                            b.Value <- 2
                            a.Value <- 3)
                    )

                Expect.equal error.Message "disk full" "the first fault"
                let! values = restored PersistOptions.defaults store
                Expect.equal values (3, 0) "the save after the faults ran"
            }

            testCaseAsync "FlushAsync reports a fault once, and the next FlushAsync covers the later saves"
            <| async {
                let store = FaultingStore [ Some "disk full"; None ]

                let! session =
                    Persist.loadAsync store declared
                    |> Async.AwaitTask

                use graph = new Graph (GraphOptions.Default.WithDispatcher (ImmediateDispatcher ()))
                use _active = graph.Activate ()
                let signal = Persist.signal session a 0
                signal.Value <- 1
                let! error = failureOf (session.FlushAsync () |> Async.AwaitTask)
                Expect.equal error.Message "disk full" "the fault is reported"
                signal.Value <- 2
                do! session.FlushAsync () |> Async.AwaitTask
                let! values = restored PersistOptions.defaults store
                Expect.equal values (2, 0) "the later save ran"
            }
            testCaseAsync "a faulted first save after a bust keeps the later saves on the next load"
            <| async {
                let store = FaultingStore [ Some "disk full" ]

                store.Inner.SaveMany [ record "$ranvier/buster" 1 "v1"; record "a" 1 "1"; record "b" 1 "2" ]
                |> ignore

                let! session =
                    Persist.loadWith (withBuster (Some "v2")) store declared
                    |> Async.AwaitTask

                use graph = new Graph (GraphOptions.Default.WithDispatcher (ImmediateDispatcher ()))
                use _active = graph.Activate ()
                let first = Persist.signal session a 0
                let second = Persist.signal session b 0
                first.Value <- 1
                let! error = failureOf (session.FlushAsync () |> Async.AwaitTask)
                Expect.equal error.Message "disk full" "the busted save faults"
                second.Value <- 2
                first.Value <- 3
                do! session.FlushAsync () |> Async.AwaitTask
                let! values = restored (withBuster (Some "v2")) store
                Expect.equal values (3, 2) "the saves after the faulted bust are restored"
            }

            testCaseAsync "a faulted delete after a bust keeps the old records discarded on the next load"
            <| async {
                let store = FaultingStore ([], [ Some "disk full" ])

                store.Inner.SaveMany [ record "$ranvier/buster" 1 "v1"; record "a" 1 "1"; record "b" 1 "2" ]
                |> ignore

                let! session =
                    Persist.loadWith (withBuster (Some "v2")) store declared
                    |> Async.AwaitTask

                use graph = new Graph (GraphOptions.Default.WithDispatcher (ImmediateDispatcher ()))
                use _active = graph.Activate ()
                let first = Persist.signal session a 0
                first.Value <- 1
                let! error = failureOf (session.FlushAsync () |> Async.AwaitTask)
                Expect.equal error.Message "disk full" "the busted delete faults"
                first.Value <- 3
                do! session.FlushAsync () |> Async.AwaitTask
                let! values = restored (withBuster (Some "v2")) store
                Expect.equal values (3, 0) "the later save is restored and the stale record stays discarded"
            }
        ]
