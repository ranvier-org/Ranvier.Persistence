module Ranvier.Persistence.Tests.Restore

open Expecto
open Ranvier
open Ranvier.Persistence
open Ranvier.Persistence.Tests.Support

let private status =
    Persist.sourceKey "status" (PersistCodec.ofJournal (intCodec 1))

let private sourceKeys: IPersistKey list = [ status ]

/// <summary>The reading as text, with a failure as its kind and message.</summary>
let private describe (reading: Reading<int>) =
    match reading with
    | Reading.Ready value -> "ready " + string value
    | Reading.Pending -> "pending"
    | Reading.Failed error ->
        match error with
        | :? PersistedFailure -> "persisted failure " + error.Message
        | _ -> "failure " + error.Message

/// <summary>Loads <c>keys</c> from <c>store</c>.</summary>
let private load (store: IStateStore) (keys: IPersistKey list) =
    Persist.loadAsync store keys |> Async.AwaitTask

/// <summary>Runs <c>body</c> in a new graph whose journal is the session's wrapper over <c>journal</c>, then waits for every save.</summary>
let private inGraph (session: PersistSession) (journal: MemoryJournal) (body: Graph -> 'T) =
    async {
        let result =
            use graph = new Graph (GraphOptions.Default.WithJournal (session.Journal journal))
            use _active = graph.Activate ()
            body graph

        do! session.FlushAsync () |> Async.AwaitTask
        return result
    }

/// <summary>The reading of a source restored for <c>status</c> from <c>store</c>, in a new graph.</summary>
let private reload (store: IStateStore) =
    async {
        let! session = load store sourceKeys
        return! inGraph session (MemoryJournal "app") (fun _ -> describe (Persist.source session status).TryValue)
    }

let private sourceTests =
    testList
        "Persist.source"
        [
            testCaseAsync "a settled value round-trips"
            <| async {
                let store = MemoryStateStore () :> IStateStore
                let! session = load store sourceKeys
                do! inGraph session (MemoryJournal "app") (fun _ -> (Persist.source session status).Settle 7)
                let! restored = reload store
                Expect.equal restored "ready 7" "the restored source holds the settled value"
            }

            testCaseAsync "a failure round-trips as a PersistedFailure carrying its message"
            <| async {
                let store = MemoryStateStore () :> IStateStore
                let! session = load store sourceKeys
                do! inGraph session (MemoryJournal "app") (fun _ -> (Persist.source session status).Fail(exn "boom"))
                let! restored = reload store
                Expect.equal restored "persisted failure boom" "the restored source fails with the stored message"
            }

            testCaseAsync "a settle after a failure replaces the stored failure"
            <| async {
                let store = MemoryStateStore () :> IStateStore
                let! session = load store sourceKeys

                do!
                    inGraph session (MemoryJournal "app") (fun _ ->
                        let source = Persist.source session status
                        source.Fail (exn "boom")
                        source.Settle 3)

                let! restored = reload store
                Expect.equal restored "ready 3" "the last status"
            }

            testCaseAsync "a pending source stores no record and restores pending"
            <| async {
                let store = RecordingStore ()
                let! session = load store sourceKeys
                do! inGraph session (MemoryJournal "app") (fun _ -> Persist.source session status |> ignore)
                Expect.equal (savedPayloads store) [] "no save"
                let! restored = reload store
                Expect.equal restored "pending" "the restored source is pending"
            }

            testCaseAsync "a settle saves the status frames at the codec's version"
            <| async {
                let store = RecordingStore ()
                let! session = load store sourceKeys
                do! inGraph session (MemoryJournal "app") (fun _ -> (Persist.source session status).Settle 7)

                Expect.equal
                    (savedPayloads store |> List.map List.sort)
                    [ [ "$ranvier/position", "1"; "status", "5:ready1:7" ] ]
                    "the ready frame and the value frame"
            }

            testCaseAsync "a restored source records no journal entry and starts settled"
            <| async {
                let store = MemoryStateStore () :> IStateStore

                do!
                    store.SaveMany [ record "status" 1 "5:ready1:7" ]
                    |> Async.AwaitTask

                let! session = load store sourceKeys
                let journal = MemoryJournal "app"

                let observed =
                    use graph = new Graph (GraphOptions.Default.WithJournal (session.Journal journal))
                    use _active = graph.Activate ()
                    let source = Persist.source session status
                    let seen = ResizeArray<string>()
                    createEffectOn (fun () -> source.TryValue) (fun reading -> seen.Add (describe reading))
                    List.ofSeq seen

                Expect.equal observed [ "ready 7" ] "the first read sees the stored value"
                Expect.equal journal.Entries [] "the restore records no entry"
            }

            testCaseAsync "a stored value frame migrates with the codec"
            <| async {
                let store = MemoryStateStore () :> IStateStore

                do!
                    store.SaveMany [ record "status" 1 "5:ready1:7" ]
                    |> Async.AwaitTask

                let migrated =
                    Persist.sourceKey
                        "status"
                        (PersistCodec.ofJournal (intCodec 2)
                         |> PersistCodec.withMigration 1 (fun payload -> Result.Ok (payload + "0")))

                let! session = load store [ migrated ]

                let! restored =
                    inGraph session (MemoryJournal "app") (fun _ -> describe (Persist.source session migrated).TryValue)

                Expect.equal restored "ready 70" "the migrated value"
            }

            testCaseAsync "a malformed status record fails the load with DecodeFailed naming the key"
            <| async {
                let store = MemoryStateStore () :> IStateStore

                do!
                    store.SaveMany [ record "status" 1 "5:maybe1:7" ]
                    |> Async.AwaitTask

                let! error = failureOf (load store sourceKeys)

                match error with
                | :? PersistLoadException as error ->
                    Expect.equal error.Key "status" "the failure names the key"

                    match error.Failure with
                    | DecodeFailed _ -> ()
                    | other -> failtestf "expected DecodeFailed, got %A" other
                | other -> failtestf "expected a PersistLoadException, got: %s" other.Message
            }

            testCaseAsync "an unframed status record fails the load with DecodeFailed"
            <| async {
                let store = MemoryStateStore () :> IStateStore

                do!
                    store.SaveMany [ record "status" 1 "7" ]
                    |> Async.AwaitTask

                do! expectLoadFailure "status" (DecodeFailed "malformed frame at offset 0") (load store sourceKeys)
            }

            testCaseAsync "DiscardUndecodable restores a malformed status record as pending"
            <| async {
                let store = MemoryStateStore () :> IStateStore

                do!
                    store.SaveMany [ record "status" 1 "5:maybe1:7" ]
                    |> Async.AwaitTask

                let! session =
                    Persist.loadWith
                        { PersistOptions.defaults with
                            DiscardUndecodable = true
                        }
                        store
                        sourceKeys
                    |> Async.AwaitTask

                let! restored =
                    inGraph session (MemoryJournal "app") (fun _ -> describe (Persist.source session status).TryValue)

                Expect.equal restored "pending" "the discarded record"
            }

            testCase "a persisted failure carries its message"
            <| fun () ->
                let error = PersistedFailure "boom"
                Expect.equal error.Message "boom" "the message"
        ]

let private aKey = Persist.key "a" (PersistCodec.ofJournal (intCodec 1))
let private bKey = Persist.key "b" (PersistCodec.ofJournal (intCodec 1))
let private signalKeys: IPersistKey list = [ aKey; bKey ]

/// <summary>The persisted signals <c>a</c> and <c>b</c>, each registered on the journal under its store key.</summary>
let private signals (session: PersistSession) =
    Journal.key "a" (intCodec 1) (Persist.signal session aKey 0), Journal.key "b" (intCodec 1) (Persist.signal session bKey 0)

/// <summary>
/// Loads <c>signalKeys</c> from <c>store</c>, builds a graph over the session's journal wrapper, and runs
/// <c>run</c> on it and its signals; returns the recorded entries.
/// </summary>
let private recorded (store: IStateStore) (run: Graph -> Signal<int> * Signal<int> -> unit) =
    async {
        let journal = MemoryJournal "app"
        let! session = load store signalKeys
        do! inGraph session journal (fun graph -> run graph (signals session))
        return journal.Entries
    }

/// <summary>
/// Loads <c>signalKeys</c> from <c>snapshot</c>, builds a graph over the session's journal wrapper, logs every value of
/// <c>a</c>, runs <c>Persist.replay mode</c> over <c>entries</c>, and returns the log, <c>b</c> and the new journal.
/// </summary>
let private restored (mode: ReplayMode) (snapshot: IStateStore) (entries: JournalEntry list) =
    async {
        let journal = MemoryJournal "app"
        let! session = load snapshot signalKeys

        let! log, b =
            inGraph session journal (fun graph ->
                let a, b = signals session
                let log = ResizeArray<int>()
                createEffectOn (fun () -> a.Value) log.Add
                Persist.replay mode session graph entries
                List.ofSeq log, b.Peek)

        return log, b, journal.Entries
    }

let private inputs (journal: JournalEntry list) =
    journal
    |> List.filter (fun entry ->
        entry.Kind <> JournalEntryKind.Flush
        && entry.Kind <> JournalEntryKind.Setup)

let private replayTests (mode: ReplayMode) =
    testList
        (string mode)
        [
            testCaseAsync "with no stored position every entry replays"
            <| async {
                let! entries =
                    recorded (MemoryStateStore ()) (fun _ (a, _) ->
                        a.Value <- 1
                        a.Value <- 2)

                let! log, _, _ = restored mode (MemoryStateStore ()) entries
                Expect.equal log [ 0; 1; 2 ] "the whole journal"
            }

            testCaseAsync "with a stored position only the later entries replay"
            <| async {
                let prefix = MemoryStateStore ()

                let! _ =
                    recorded prefix (fun _ (a, b) ->
                        a.Value <- 1
                        b.Value <- 4)

                let! entries =
                    recorded (MemoryStateStore ()) (fun _ (a, b) ->
                        a.Value <- 1
                        b.Value <- 4
                        a.Value <- 2)

                let! log, b, journal = restored mode prefix entries
                Expect.equal log [ 1; 2 ] "the snapshot's value, then the tail"
                Expect.equal b 4 "the snapshot's value"
                Expect.equal (inputs journal |> List.map _.Seq) [ 1 ] "the tail is recorded from Seq 1"
            }

            testCaseAsync "entries without the stored position raise ArgumentException naming it"
            <| async {
                let prefix = MemoryStateStore ()
                let! _ = recorded prefix (fun _ (a, _) -> a.Value <- 1)
                let! error = failureOf (restored mode prefix [])

                match error with
                | :? System.ArgumentException -> Expect.stringContains error.Message "position 1" "the message names the position"
                | other -> failtestf "expected an ArgumentException, got: %s" other.Message
            }

            testCaseAsync "an input of work started before the position is applied once"
            <| async {
                let prefix = MemoryStateStore ()
                let! _ = recorded prefix (fun _ (a, _) -> a.Value <- 1)

                let! entries =
                    recorded (MemoryStateStore ()) (fun graph (a, _) ->
                        a.Value <- 1
                        let work = Journal.work graph
                        Journal.produce work (fun () -> a.Value <- 2))

                match inputs entries |> List.map _.Origin with
                | [ JournalOrigin.External; JournalOrigin.Produced (1, _, 0) ] -> ()
                | origins -> failtestf "expected an input of work started after entry 1, got %A" origins

                let! log, _, journal = restored mode prefix entries
                Expect.equal log [ 1; 2 ] "the input is applied once"
                Expect.equal (inputs journal |> List.map _.Origin) [ JournalOrigin.External ] "the input is recorded as external"
            }

            testCaseAsync "an input of work started before the position raises when the restored graph re-runs setup work"
            <| async {
                // An effect over a starts a work for each positive value; the test produces the last one's input.
                let run (produce: bool) (graph: Graph) (a: Signal<int>, b: Signal<int>) =
                    let mutable pending = None

                    createEffectOn (fun () -> a.Value) (fun value ->
                        if value > 0 then
                            pending <- Some (Journal.work graph))

                    a.Value <- 1

                    if produce then
                        Journal.produce pending.Value (fun () -> b.Value <- b.Peek + 1)

                let prefix = MemoryStateStore ()
                let! _ = recorded prefix (run false)
                let! entries = recorded (MemoryStateStore ()) (run true)

                match inputs entries |> List.map _.Origin with
                | [ JournalOrigin.External; JournalOrigin.Produced (1, _, 0) ] -> ()
                | origins -> failtestf "expected an input of work started after entry 1, got %A" origins

                let! session = load prefix signalKeys

                let! error, b =
                    inGraph session (MemoryJournal "app") (fun graph ->
                        let a, b = signals session

                        createEffectOn (fun () -> a.Value) (fun value ->
                            if value > 0 then
                                Journal.work graph |> ignore)

                        let error =
                            try
                                Persist.replay mode session graph entries
                                None
                            with error ->
                                Some error

                        error, b.Peek)

                match error with
                | Some (:? JournalReplayException as error) -> Expect.stringContains error.Reason "setup work" "the reason names setup work"
                | Some other -> failtestf "expected a JournalReplayException, got: %s" other.Message
                | None -> failtest "the replay applied an input the restored graph's setup work re-produces"

                Expect.equal b 0 "no entry applied"
            }

            testCaseAsync "an input of setup work after the position raises before any entry applies"
            <| async {
                let setupRun (more: bool) (graph: Graph) (a: Signal<int>, b: Signal<int>) =
                    let work = Journal.work graph
                    a.Value <- 1

                    if more then
                        b.Value <- 5
                        Journal.produce work (fun () -> a.Value <- 2)

                let prefix = MemoryStateStore ()
                let! _ = recorded prefix (setupRun false)
                let! entries = recorded (MemoryStateStore ()) (setupRun true)
                let! session = load prefix signalKeys

                let! error, b =
                    inGraph session (MemoryJournal "app") (fun graph ->
                        let _, b = signals session

                        let error =
                            try
                                Persist.replay mode session graph entries
                                None
                            with error ->
                                Some error

                        error, b.Peek)

                match error with
                | Some (:? JournalReplayException as error) -> Expect.stringContains error.Reason "input of setup work" "the reason names setup work"
                | Some other -> failtestf "expected a JournalReplayException, got: %s" other.Message
                | None -> failtest "the replay applied an input of setup work"

                Expect.equal b 0 "no entry applied"
            }
        ]

/// <summary>An input to the restored application.</summary>
type RestoreOp =
    | WriteA of int
    | WriteB of int
    | WriteBoth of int * int
    | Settle of int
    | Fail of int
    | Flush

/// <summary>The keyed inputs and memos of the application a restore rebuilds.</summary>
type private App =
    {
        A: Signal<int>
        B: Signal<int>
        Status: AsyncSource<int>
        Sum: Memo<int>
        Scaled: Memo<int>
    }

let private appKeys: IPersistKey list = [ aKey; bKey; status ]

/// <summary>The application, its every keyed input persisted and registered on the journal under its store key.</summary>
let private build (session: PersistSession) =
    let a, b = signals session

    let source = Journal.keySource "status" (intCodec 1) (Persist.source session status)

    {
        A = a
        B = b
        Status = source
        Sum = createMemo (fun _ -> a.Value + b.Value)
        Scaled = createMemo (fun _ -> source.Value * 10 + a.Value)
    }

let private perform (app: App) (op: RestoreOp) =
    match op with
    | WriteA value -> app.A.Value <- value
    | WriteB value -> app.B.Value <- value
    | WriteBoth (a, b) ->
        batch (fun () ->
            app.A.Value <- a
            app.B.Value <- b)
    | Settle value -> app.Status.Settle value
    | Fail code -> app.Status.Fail (exn ("failure " + string code))
    | Flush -> flush ()

/// <summary>The reading as text, with a failure as its message.</summary>
let private outcome (reading: Reading<int>) =
    match reading with
    | Reading.Ready value -> "ready " + string value
    | Reading.Pending -> "pending"
    | Reading.Failed error -> "failed " + error.Message

/// <summary>The values of every keyed input and every memo of the application.</summary>
let private stateOf (app: App) =
    [
        "a " + string app.A.Peek
        "b " + string app.B.Peek
        "status " + outcome app.Status.TryValue
        "sum " + outcome app.Sum.TryValue
        "scaled " + outcome app.Scaled.TryValue
    ]

/// <summary>Runs <c>ops</c> on the application over <c>store</c>; returns its final state and its journal.</summary>
let private runApp (store: IStateStore) (ops: RestoreOp list) =
    async {
        let journal = MemoryJournal "app"
        let! session = load store appKeys

        let! final =
            inGraph session journal (fun _ ->
                let app = build session
                List.iter (perform app) ops
                stateOf app)

        return final, journal.Entries
    }

/// <summary>
/// Expects the application restored from the snapshot taken after the first <c>cut</c> ops, with the uninterrupted
/// run's journal replayed by <c>Persist.replay mode</c>, to hold the uninterrupted run's final state.
/// </summary>
let restoreMatches (mode: ReplayMode) (ops: RestoreOp list) (cut: int) =
    async {
        let snapshot = MemoryStateStore ()
        let! _ = runApp snapshot (List.truncate cut ops)
        let! expected, entries = runApp (MemoryStateStore ()) ops
        let! session = load snapshot appKeys

        let! actual =
            inGraph session (MemoryJournal "app") (fun graph ->
                let app = build session
                Persist.replay mode session graph entries
                stateOf app)

        Expect.equal
            actual
            expected
            ("restored after "
             + string cut
             + " of "
             + string ops
             + " in "
             + string mode)
    }

/// <summary>
/// <c>count</c> op lists of up to 12 ops, each with a cut point, from a Park-Miller generator seeded with <c>seed</c>.
/// </summary>
let seededCases (seed: int) (count: int) =
    let mutable state = float (max 1 seed)

    let next (bound: int) =
        state <- (state * 48271.0) % 2147483647.0
        int (state % float bound)

    [
        for _ in 1..count do
            let ops =
                [
                    for _ in 1 .. next 13 do
                        match next 6 with
                        | 0 -> WriteA (next 4)
                        | 1 -> WriteB (next 4)
                        | 2 -> WriteBoth (next 4, next 4)
                        | 3 -> Settle (next 4)
                        | 4 -> Fail (next 3)
                        | _ -> Flush
                ]

            ops, next (ops.Length + 1)
    ]

let private propertyTests =
    testList
        "restore property"
        [
            for mode in [ ReplayMode.Diagnostic; ReplayMode.Reenact ] do
                testCaseAsync (
                    "seeded op lists restore to the uninterrupted run under "
                    + string mode
                )
                <| async {
                    for ops, cut in seededCases 197 80 do
                        do! restoreMatches mode ops cut
                }
#if !FABLE_COMPILER
                testPropertyWithConfig
                    { FsCheckConfig.defaultConfig with
                        maxTest = 200
                    }
                    ("op lists restore to the uninterrupted run under "
                     + string mode)
                    (fun (ops: RestoreOp list) (cut: int) ->
                        restoreMatches mode ops (abs (cut % (ops.Length + 1)))
                        |> Async.RunSynchronously)
#endif
        ]

[<Tests>]
let tests =
    testList
        "Restore"
        [
            sourceTests
            testList "Persist.replay" [ replayTests ReplayMode.Diagnostic; replayTests ReplayMode.Reenact ]
            propertyTests
        ]
