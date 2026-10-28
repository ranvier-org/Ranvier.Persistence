module Ranvier.Persistence.Tests.Position

open Expecto
open Ranvier
open Ranvier.Persistence
open Ranvier.Persistence.Tests.Support

let private a = Persist.key "a" (PersistCodec.ofJournal (intCodec 1))
let private b = Persist.key "b" (PersistCodec.ofJournal (intCodec 1))
let private declared: IPersistKey list = [ a; b ]

/// <summary>The <c>Seq</c> of the last entry of <c>journal</c> other than a <c>Flush</c>.</summary>
let private lastInput (journal: MemoryJournal) =
    journal.Entries
    |> List.filter (fun entry -> entry.Kind <> JournalEntryKind.Flush)
    |> List.last
    |> _.Seq

/// <summary>
/// Loads <c>a</c> and <c>b</c>, builds a graph whose journal is the session's wrapper over <c>journal</c>, runs
/// <c>write</c> on the persisted signals and a plain signal, then waits for every save.
/// </summary>
let private journaled (store: IStateStore) (journal: MemoryJournal) (write: Signal<int> -> Signal<int> -> Signal<int> -> unit) =
    async {
        let! session =
            Persist.loadAsync store declared
            |> Async.AwaitTask

        do
            use graph = new Graph (GraphOptions.Default.WithJournal (session.Journal journal))
            use _active = graph.Activate ()
            write (Persist.signal session a 0) (Persist.signal session b 0) (createSignal 0)

        do! session.FlushAsync () |> Async.AwaitTask
        return session
    }

/// <summary>
/// A memory store whose <c>SaveMany</c> writes its records one at a time, in list order, and faults on the record at
/// index <c>faultAt</c> of the first call.
/// </summary>
type private PartialStore(faultAt: int) =
    let inner = MemoryStateStore () :> IStateStore
    let mutable faulted = false

    interface IStateStore with
        member _.LoadMany keys =
            inner.LoadMany keys

        member _.SaveMany records =
            if faulted then
                inner.SaveMany records
            else
                faulted <- true

                inner.SaveMany (List.truncate faultAt records)
                |> ignore

                failed "disk full"

        member _.DeleteMany keys =
            inner.DeleteMany keys

        member _.Clear() =
            inner.Clear ()

[<Tests>]
let tests =
    testList
        "Position"
        [
            testCaseAsync "a save records the position of the last journal entry in the same SaveMany"
            <| async {
                let store = RecordingStore ()
                let journal = MemoryJournal "app"

                let! _ =
                    journaled store journal (fun a b _ ->
                        batch (fun () ->
                            a.Value <- 1
                            b.Value <- 2))

                let position = lastInput journal
                Expect.equal position 2 "two inputs were journaled"

                Expect.equal
                    (savedPayloads store |> List.map List.sort)
                    [ [ "$ranvier/position", "2"; "a", "1"; "b", "2" ] ]
                    "one save holding the records and the position"
            }

            testCaseAsync "a load exposes the saved position"
            <| async {
                let store = MemoryStateStore () :> IStateStore
                let journal = MemoryJournal "app"

                let! _ =
                    journaled store journal (fun a _ plain ->
                        a.Value <- 1
                        plain.Value <- 5
                        a.Value <- 2)

                let! session =
                    Persist.loadAsync store declared
                    |> Async.AwaitTask

                Expect.equal session.Position (Some (lastInput journal)) "the position of the last saved input"
                Expect.equal (Persist.restore session a 0) 2 "the saved value"
            }

            testCaseAsync "inputs journaled after the last save are not included in the position"
            <| async {
                let store = MemoryStateStore () :> IStateStore
                let journal = MemoryJournal "app"
                let mutable saved = 0

                let! _ =
                    journaled store journal (fun a _ plain ->
                        a.Value <- 1
                        saved <- lastInput journal
                        plain.Value <- 5)

                Expect.isGreaterThan (lastInput journal) saved "the plain write was journaled after the save"

                let! session =
                    Persist.loadAsync store declared
                    |> Async.AwaitTask

                Expect.equal session.Position (Some saved) "the position of the last saved input"
            }

            testCaseAsync "the wrapper forwards every entry to the inner journal"
            <| async {
                let store = MemoryStateStore () :> IStateStore
                let journal = MemoryJournal "app"

                let! session =
                    Persist.loadAsync store declared
                    |> Async.AwaitTask

                let wrapper = session.Journal journal
                Expect.equal wrapper.Stream "app" "the inner stream"

                do
                    use graph = new Graph (GraphOptions.Default.WithJournal wrapper)
                    use _active = graph.Activate ()
                    let plain = createSignal 0
                    plain.Value <- 1

                Expect.isNonEmpty journal.Entries "the inner journal records the entries"
            }

            testCaseAsync "a session without a journal saves no position"
            <| async {
                let store = RecordingStore ()

                let! session =
                    Persist.loadAsync store declared
                    |> Async.AwaitTask

                do
                    use graph = new Graph (GraphOptions.Default)
                    use _active = graph.Activate ()
                    (Persist.signal session a 0).Value <- 1

                do! session.FlushAsync () |> Async.AwaitTask
                Expect.equal (savedPayloads store) [ [ "a", "1" ] ] "the record only"

                let! reloaded =
                    Persist.loadAsync store declared
                    |> Async.AwaitTask

                Expect.equal reloaded.Position None "no position"
            }

            testCaseAsync "a save that faults part way leaves no position ahead of the records"
            <| async {
                let store = PartialStore 1 :> IStateStore
                let journal = MemoryJournal "app"

                let! session =
                    Persist.loadAsync store declared
                    |> Async.AwaitTask

                do
                    use graph = new Graph (GraphOptions.Default.WithJournal (session.Journal journal))
                    use _active = graph.Activate ()

                    batch (fun () ->
                        (Persist.signal session a 0).Value <- 1
                        (Persist.signal session b 0).Value <- 2)

                let! error = failureOf (session.FlushAsync () |> Async.AwaitTask)
                Expect.equal error.Message "disk full" "the save faults on its second record"

                let! reloaded =
                    Persist.loadAsync store declared
                    |> Async.AwaitTask

                Expect.equal reloaded.Position None "the position is written after every record"
            }

            testCaseAsync "a save without a journal deletes an earlier session's position"
            <| async {
                let store = MemoryStateStore () :> IStateStore
                let! _ = journaled store (MemoryJournal "app") (fun a _ _ -> a.Value <- 1)

                let! session =
                    Persist.loadAsync store declared
                    |> Async.AwaitTask

                Expect.notEqual session.Position None "the first session saved a position"

                do
                    use graph = new Graph (GraphOptions.Default)
                    use _active = graph.Activate ()
                    (Persist.signal session a 0).Value <- 5

                do! session.FlushAsync () |> Async.AwaitTask

                let! reloaded =
                    Persist.loadAsync store declared
                    |> Async.AwaitTask

                Expect.equal reloaded.Position None "no position describes the newer record"
                Expect.equal (Persist.restore reloaded a 0) 5 "the newer record"
            }

            testCaseAsync "a persisted signal written by an effect saves in the flush's one SaveMany"
            <| async {
                let store = RecordingStore ()
                let journal = MemoryJournal "app"

                let! session =
                    Persist.loadAsync store declared
                    |> Async.AwaitTask

                do
                    use graph = new Graph (GraphOptions.Default.WithJournal (session.Journal journal))
                    use _active = graph.Activate ()
                    let derived = Persist.signal session b 0
                    let plain = createSignal 0
                    createEffectOn (fun () -> plain.Value) (fun value -> derived.Value <- value * 10)
                    let direct = Persist.signal session a 0

                    batch (fun () ->
                        direct.Value <- 1
                        plain.Value <- 1)

                do! session.FlushAsync () |> Async.AwaitTask

                Expect.equal
                    (savedPayloads store |> List.map List.sort)
                    [ [ "$ranvier/position", string (lastInput journal); "a", "1"; "b", "10" ] ]
                    "one save holding both records and the position"
            }
        ]
