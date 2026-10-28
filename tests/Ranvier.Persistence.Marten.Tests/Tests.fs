module Ranvier.Persistence.Marten.Tests.Tests

open System
open System.Threading.Tasks
open Expecto
open Marten
open Ranvier
open Ranvier.Persistence
open Ranvier.Persistence.Marten
open Ranvier.Persistence.Tests

let private connect () =
    let connection = Environment.GetEnvironmentVariable "RANVIER_MARTEN_CONNECTION"

    if String.IsNullOrWhiteSpace connection then
        failwith "Set RANVIER_MARTEN_CONNECTION to an isolated PostgreSQL test database."

    DocumentStore.For connection

let private database = lazy (connect ())

let private scope () =
    Guid.NewGuid().ToString("N")

let private store name =
    MartenStateStore (database.Value, name) :> IStateStore

let private create () =
    store (scope ())

let private record key payload =
    Support.record key 1 payload

let private load (store: IStateStore) keys =
    store.LoadMany keys |> Async.AwaitTask

let private save (store: IStateStore) rows =
    store.SaveMany rows |> Async.AwaitTask

let private isConflict (error: exn) =
    match error with
    | :? JasperFx.ConcurrencyException -> true
    | :? AggregateException as batch ->
        let errors = batch.Flatten().InnerExceptions

        errors.Count > 0
        && (errors
            |> Seq.forall (fun item -> item :? JasperFx.ConcurrencyException))
    | _ -> false

let private conflict work =
    async {
        let! result = work |> Async.Catch

        match result with
        | Choice1Of2 _ -> failtest "the conflicting write succeeded"
        | Choice2Of2 error when isConflict error -> ()
        | Choice2Of2 error -> failtestf "expected a concurrency conflict, got %A" error
    }

let private cases =
    [
        testCaseAsync "a deleted record reports a concurrency conflict"
        <| async {
            let store = create ()
            do! save store [ record "count" "1" ]
            let! loaded = load store [ "count" ]
            do! store.DeleteMany [ "count" ] |> Async.AwaitTask

            do!
                save store [ { loaded.Head with Payload = "2" } ]
                |> conflict
        }

        testCaseAsync "a conflict after a batch page rolls back the earlier pages"
        <| async {
            let store = create ()
            do! save store [ record "count" "1" ]
            let! old = load store [ "count" ]
            do! save store [ { old.Head with Payload = "2" } ]

            let rows =
                [
                    for index in 0 .. database.Value.Options.UpdateBatchSize -> record (sprintf "new/%04d" index) "created"
                ]

            do!
                save store (rows @ [ { old.Head with Payload = "stale" } ])
                |> conflict

            let! inserted = load store (rows |> List.map _.Key)
            let! kept = load store [ "count" ]
            Expect.isEmpty inserted "all earlier pages roll back"
            Expect.equal kept.Head.Payload "2" "the accepted version remains"
        }

        testCaseAsync "a mixed batch commits conditional and unconditional records together"
        <| async {
            let store = create ()
            do! save store [ record "count" "1" ]
            let! original = load store [ "count" ]
            do! save store [ record "new" "created"; { original.Head with Payload = "2" } ]
            let! loaded = load store [ "count"; "new" ]

            Expect.equal
                (loaded
                 |> List.map (fun row -> row.Key, row.Payload)
                 |> Set.ofList)
                (Set.ofList [ "count", "2"; "new", "created" ])
                "both writes commit"
        }

        testCaseAsync "invalid Unicode keys cannot collide with a replacement character"
        <| async {
            let store = create ()
            do! save store [ record "�" "kept" ]

            let! error =
                save store [ record (string (char 0xD800)) "invalid" ]
                |> Support.failureOf

            Expect.isTrue (error :? System.Text.EncoderFallbackException) "invalid UTF-16 is rejected"
            let! loaded = load store [ "�" ]
            Expect.equal loaded.Head.Payload "kept" "encoding cannot silently substitute a key"
        }

        testCaseAsync "one conflicting record rolls back unconditional writes in its batch"
        <| async {
            let store = create ()
            do! save store [ record "count" "1" ]
            let! old = load store [ "count" ]
            do! save store [ { old.Head with Payload = "2" } ]

            do!
                save store [ record "new" "created"; { old.Head with Payload = "3" } ]
                |> conflict

            let! loaded = load store [ "new"; "count" ]

            Expect.equal
                (loaded
                 |> List.map (fun row -> row.Key, row.Payload))
                [ "count", "2" ]
                "the entire batch rolls back"
        }

        testCaseAsync "independent store instances racing with one ETag accept exactly one writer"
        <| async {
            let name = scope ()
            let first = store name
            use otherDatabase = connect ()
            let second = MartenStateStore (otherDatabase, name) :> IStateStore
            do! save first [ record "count" "0" ]
            let! loaded = load first [ "count" ]

            let start =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let attempt store payload =
                async {
                    do! start.Task |> Async.AwaitTask

                    let! result =
                        save store [ { loaded.Head with Payload = payload } ]
                        |> Async.Catch

                    return
                        match result with
                        | Choice1Of2 _ -> true
                        | Choice2Of2 error when isConflict error -> false
                        | Choice2Of2 error -> raise error
                }

            let running =
                Async.StartAsTask (Async.Parallel [ attempt first "left"; attempt second "right" ])

            start.SetResult ()
            let! accepted = running |> Async.AwaitTask
            Expect.equal (accepted |> Array.filter id |> Array.length) 1 "one winner"
            let! final = load first [ "count" ]
            Expect.equal final.Head.Payload (if accepted[0] then "left" else "right") "the winning payload is durable"
        }

        testCaseAsync "clear and delete stay inside their application scope"
        <| async {
            let left, right = create (), create ()
            do! save left [ record "same" "left"; record "$ranvier/buster" "left" ]
            do! save right [ record "same" "right"; record "$ranvier/buster" "right" ]
            do! left.DeleteMany [ "same" ] |> Async.AwaitTask
            let! rightAfterDelete = load right [ "same" ]
            Expect.equal rightAfterDelete.Head.Payload "right" "deletion respects the tenant"
            do! left.Clear () |> Async.AwaitTask
            let! removed = load left [ "same"; "$ranvier/buster" ]
            let! kept = load right [ "same"; "$ranvier/buster" ]
            Expect.isEmpty removed "clear includes reserved metadata in its tenant"
            Expect.equal (kept |> List.map _.Payload) [ "right"; "right" ] "clear keeps the other tenant"
        }

        testCaseAsync "empty keys and Unicode normalization variants remain distinct"
        <| async {
            let store = create ()

            do!
                save
                    store
                    [
                        record "" "empty"
                        record "k" "prefix"
                        record "é" "composed"
                        record "é" "decomposed"
                    ]

            let! loaded = load store [ ""; ""; "k"; "é"; "é" ]

            Expect.equal
                (loaded
                 |> List.map (fun row -> row.Key, row.Payload)
                 |> Set.ofList)
                (Set.ofList [ "", "empty"; "k", "prefix"; "é", "composed"; "é", "decomposed" ])
                "identities preserve the original key and duplicate loads return it once"
        }

        testCaseAsync "an unconditional save after a conditional save still replaces the record"
        <| async {
            let store = create ()
            do! save store [ record "count" "1" ]
            let! first = load store [ "count" ]
            do! save store [ { first.Head with Payload = "2" } ]
            do! save store [ record "count" "3" ]
            let! last = load store [ "count" ]
            Expect.equal last.Head.Payload "3" "None keeps last-writer-wins"

            do!
                save store [ { first.Head with Payload = "stale" } ]
                |> conflict
        }

        testCaseAsync "a batch containing duplicate keys is rejected before it writes"
        <| async {
            let store = create ()

            let! error =
                save store [ record "a" "first"; record "a" "second" ]
                |> Support.failureOf

            Expect.isTrue (error :? ArgumentException) "duplicates have no implicit ordering"
            let! loaded = load store [ "a" ]
            Expect.isEmpty loaded "a rejected batch leaves the store unchanged"
        }

        testCaseAsync "malformed ETags cannot partially write a batch"
        <| async {
            let store = create ()

            let! error =
                save
                    store
                    [
                        record "a" "first"
                        { record "b" "second" with
                            ETag = Some "invalid"
                        }
                    ]
                |> Support.failureOf

            Expect.isTrue (error :? ArgumentException) "invalid token"
            let! loaded = load store [ "a"; "b" ]
            Expect.isEmpty loaded "validation happens before commit"
        }

        testCaseAsync "the empty GUID is a conditional token rather than an unconditional save"
        <| async {
            let store = create ()
            do! save store [ record "a" "original" ]

            do!
                save
                    store
                    [
                        { record "a" "replacement" with
                            ETag = Some (Guid.Empty.ToString ("D"))
                        }
                    ]
                |> conflict

            let! loaded = load store [ "a" ]
            Expect.equal loaded.Head.Payload "original" "Some never opts out of concurrency"
        }

        testCase "empty operations complete without opening a connection"
        <| fun () ->
            use offline =
                DocumentStore.For "Host=127.0.0.1;Port=1;Database=offline;Username=offline;Timeout=1"

            let store = MartenStateStore (offline, "empty") :> IStateStore
            Expect.isEmpty ((store.LoadMany []).GetAwaiter().GetResult()) "empty load"
            (store.SaveMany []).GetAwaiter().GetResult()
            (store.DeleteMany []).GetAwaiter().GetResult()

        testCase "a fresh graph restores two successive persisted writes"
        <| fun () ->
            let name = scope ()
            let key = Persist.key "count" (PersistCodec.ofJournal (Support.intCodec 1))
            let session = (Persist.loadAsync (store name) [ key ]).GetAwaiter().GetResult()

            let write () =
                use graph = new Graph ()
                use active = graph.Activate ()
                let count = Persist.signal session key 0
                count.Value <- 1
                session.FlushAsync().GetAwaiter().GetResult()
                count.Value <- 2
                session.FlushAsync ()

            (write ()).GetAwaiter().GetResult()
            use reconnected = connect ()
            let restoredStore = MartenStateStore (reconnected, name) :> IStateStore
            let restored = (Persist.loadAsync restoredStore [ key ]).GetAwaiter().GetResult()
            use graph = new Graph ()
            use active = graph.Activate ()
            let count = Persist.signal restored key 0
            let doubled = createMemo (fun _ -> count.Value * 2)
            Expect.equal count.Value 2 "first read restores the stored input"
            Expect.equal doubled.Value 4 "derived state recomputes"

        testCase "blank scopes and a null database fail before storage access"
        <| fun () ->
            Expect.throwsT<ArgumentNullException>
                (fun () ->
                    MartenStateStore (Unchecked.defaultof<IDocumentStore>, "app")
                    |> ignore)
                "database"

            Expect.throwsT<ArgumentException> (fun () -> MartenStateStore (database.Value, " ") |> ignore) "scope"
    ]

[<Tests>]
let tests =
    testSequenced (
        testList
            "Marten state store"
            [
                StoreConformance.suite "shared" create
                testList "optimistic" (OptimisticStoreConformance.optimisticStoreCases create)
                testList "PostgreSQL" cases
            ]
    )
