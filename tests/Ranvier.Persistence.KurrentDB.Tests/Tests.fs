module Ranvier.Persistence.KurrentDB.Tests.Tests

open System
open System.IO
open System.Text
open System.Threading.Tasks
open Expecto
open KurrentDB.Client
open Ranvier
open Ranvier.Persistence
open Ranvier.Persistence.KurrentDB
open Ranvier.Persistence.Tests

let private connect () =
    let connection = Environment.GetEnvironmentVariable "RANVIER_KURRENTDB_CONNECTION"

    if String.IsNullOrWhiteSpace connection then
        failwith "Set RANVIER_KURRENTDB_CONNECTION to run the KurrentDB integration suite."

    new KurrentDBClient (KurrentDBClientSettings.Create connection)

let private client = lazy (connect ())

let private scope () =
    Guid.NewGuid().ToString("N")

let private store name =
    KurrentDBStateStore (client.Value, name) :> IStateStore

let private create () =
    store (scope ())

let private record key payload : StoredRecord =
    {
        Key = key
        Version = 1
        Payload = payload
        ETag = None
    }

let private load (store: IStateStore) keys =
    store.LoadMany keys |> Async.AwaitTask

let private save (store: IStateStore) records =
    store.SaveMany records |> Async.AwaitTask

let private unwrap (error: exn) =
    match error with
    | :? AggregateException as aggregate when aggregate.Flatten().InnerExceptions.Count = 1 -> aggregate.Flatten().InnerExceptions[0]
    | _ -> error

let private failure operation =
    async {
        let! result = Async.Catch operation

        match result with
        | Choice1Of2 _ -> return failtest "The operation unexpectedly succeeded."
        | Choice2Of2 error -> return unwrap error
    }

let private conflict operation =
    async {
        let! error = failure operation
        Expect.isTrue (error :? KurrentDBStateConflictException) (string error)
    }

let private stream (name: string) =
    "ranvier-state-v1-"
    + Convert.ToBase64String(Encoding.UTF8.GetBytes name).TrimEnd('=').Replace('+', '-').Replace('/', '_')

let private cases =
    [
        testCaseAsync "a mixed conditional batch commits and preserves unrelated ETags"
        <| async {
            let target = create ()
            do! save target [ record "a" "one"; record "b" "two" ]
            let! before = load target [ "a"; "b" ]
            let a = before |> List.find (fun r -> r.Key = "a")
            let b = before |> List.find (fun r -> r.Key = "b")
            do! save target [ { a with Payload = "updated" }; record "c" "three" ]
            let! after = load target [ "a"; "b"; "c" ]
            Expect.equal after.Length 3 "the mixed batch committed"
            Expect.equal (after |> List.find (fun r -> r.Key = "b")) b "unrelated token survives"
            do! save target [ { b with Payload = "still conditional" } ]
        }
        testCaseAsync "a stale batch changes no records"
        <| async {
            let target = create ()
            do! save target [ record "a" "one"; record "b" "two" ]
            let! loaded = load target [ "b" ]
            do! save target [ record "b" "winner" ]

            do!
                conflict (
                    save
                        target
                        [
                            record "a" "partial"
                            { loaded.Head with Payload = "loser" }
                            record "c" "partial"
                        ]
                )

            let! result = load target [ "a"; "b"; "c" ]

            Expect.equal
                (result
                 |> List.map (fun r -> r.Key, r.Payload)
                 |> Map.ofList)
                (Map.ofList [ "a", "one"; "b", "winner" ])
                "the batch rolled back"
        }
        testCaseAsync "deleted and cleared keys reject their former tokens"
        <| async {
            let target = create ()

            for clear in [ false; true ] do
                do! save target [ record "a" "first" ]
                let! loaded = load target [ "a" ]

                if clear then
                    do! target.Clear () |> Async.AwaitTask
                else
                    do! target.DeleteMany [ "a" ] |> Async.AwaitTask

                do! conflict (save target loaded)
                do! save target [ record "a" "replacement" ]
                do! conflict (save target loaded)
                let! current = load target [ "a" ]
                Expect.equal current.Head.Payload "replacement" "recreation rotates identity"
        }
        testCaseAsync "independent clients competing on an ETag have one winner"
        <| async {
            let name = scope ()
            let target = store name
            do! save target [ record "a" "seed" ]
            let! initial = load target [ "a" ]
            use second = connect ()
            let other = KurrentDBStateStore (second, name) :> IStateStore

            let barrier =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let write (target: IStateStore) payload =
                task {
                    do! barrier.Task

                    try
                        do! target.SaveMany [ { initial.Head with Payload = payload } ]
                        return None
                    with error ->
                        return Some error
                }

            let first = write target "first"
            let secondWrite = write other "second"
            barrier.SetResult ()

            let! results =
                Task.WhenAll [| first; secondWrite |]
                |> Async.AwaitTask

            Expect.equal
                (results
                 |> Array.filter Option.isNone
                 |> Array.length)
                1
                "one commit"

            let errors = results |> Array.choose id
            Expect.isTrue (errors[0] :? KurrentDBStateConflictException) (string errors[0])
            let! final = load target [ "a" ]
            Expect.contains [ "first"; "second" ] final.Head.Payload "winner remains"
        }
        testCaseAsync "concurrent unconditional writes preserve every distinct key"
        <| async {
            let name = scope ()

            let barrier =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let writes =
                [|
                    for i in 1..16 ->
                        task {
                            do! barrier.Task
                            do! (store name).SaveMany [ record (string i) (string i) ]
                        }
                |]

            barrier.SetResult ()
            let! _ = Task.WhenAll writes |> Async.AwaitTask
            let! loaded = load (store name) [ for i in 1..16 -> string i ]
            Expect.equal loaded.Length 16 "CAS retries preserve unrelated writes"
        }
        testCaseAsync "scope clear and delete isolate reserved metadata and Unicode scopes"
        <| async {
            let root = scope ()
            let first = store (root + "/é")
            let second = store (root + "/e\u0301")

            for target in [ first; second ] do
                do! save target [ record "a" "value"; record "__ranvier/buster" "metadata" ]

            do! first.DeleteMany [ "a" ] |> Async.AwaitTask
            let! other = load second [ "a" ]
            Expect.equal other.Length 1 "delete stays scoped"
            do! first.Clear () |> Async.AwaitTask
            let! cleared = load first [ "__ranvier/buster" ]
            let! preserved = load second [ "a"; "__ranvier/buster" ]
            Expect.isEmpty cleared "clear removes scoped metadata"
            Expect.equal preserved.Length 2 "other scope survives"
        }
        testCaseAsync "empty and ordinal Unicode keys remain distinct"
        <| async {
            let target = create ()
            let keys = [ ""; "A"; "a"; "é"; "e\u0301"; "k"; "a/b"; "🙂" ]
            do! save target (keys |> List.map (fun key -> record key key))
            let! loaded = load target (keys @ keys)
            Expect.equal (loaded |> List.map (fun r -> r.Key) |> Set.ofList) (Set.ofList keys) "lossless keys"
            Expect.equal loaded.Length keys.Length "duplicate reads are deduplicated"
        }
        testCaseAsync "duplicate keys and malformed tags reject a whole batch"
        <| async {
            let target = create ()
            do! save target [ record "a" "seed" ]

            let! duplicate =
                failure (async { do! save target [ record "a" "first"; record "a" "second" ] })

            Expect.isTrue (duplicate :? ArgumentException) "duplicate batch"

            let! malformed =
                failure (
                    async {
                        do!
                            save
                                target
                                [
                                    record "b" "partial"
                                    { record "a" "bad" with
                                        ETag = Some "bad"
                                    }
                                ]
                    }
                )

            Expect.isTrue (malformed :? ArgumentException) "malformed token"
            let! loaded = load target [ "a"; "b" ]
            Expect.equal (loaded |> List.map (fun r -> r.Payload)) [ "seed" ] "no partial writes"
        }
        testCaseAsync "invalid UTF-16 keys and payloads fail without replacement"
        <| async {
            let target = create ()
            let invalid = String (char 0xD800, 1)

            for rows in [ [ record invalid "bad" ]; [ record "ok" invalid ] ] do
                let! error = failure (async { do! save target rows })
                Expect.isTrue (error :? EncoderFallbackException) "invalid Unicode"

            let! loaded = load target [ "ok"; "�" ]
            Expect.isEmpty loaded "replacement data was not stored"
        }
        testCaseAsync "an oversized snapshot fails atomically"
        <| async {
            let target = create ()
            do! save target [ record "a" "seed" ]

            let! error =
                failure (save target [ record "a" "partial"; record "huge" (String ('x', 2 * 1024 * 1024)) ])

            Expect.isTrue (error :? InvalidOperationException) "snapshot capacity limit"
            let! loaded = load target [ "a"; "huge" ]
            Expect.equal (loaded |> List.map (fun r -> r.Payload)) [ "seed" ] "oversized batch did not commit"
        }
        testCaseAsync "foreign and unsupported snapshot heads fail closed"
        <| async {
            for eventType, json in
                [
                    "other-event", "{}"
                    "ranvier-state-snapshot-v1", """{"Schema":2,"Records":[]}"""
                    "ranvier-state-snapshot-v1", """{"Schema":1,"Records":[null]}"""
                ] do
                let name = scope ()
                let data = EventData (Uuid.NewUuid (), eventType, Encoding.UTF8.GetBytes json)

                let! _ =
                    client.Value.AppendToStreamAsync (stream name, StreamState.NoStream, [ data ])
                    |> Async.AwaitTask

                let! error = failure (load (store name) [ "a" ])
                Expect.isTrue (error :? InvalidDataException) (string error)
                let! writeError = failure (save (store name) [ record "a" "replacement" ])
                Expect.isTrue (writeError :? InvalidDataException) "foreign data is not overwritten"
        }
        testCase "empty operations avoid a connection"
        <| fun () ->
            use offline =
                new KurrentDBClient (KurrentDBClientSettings.Create ("kurrentdb://127.0.0.1:1?tls=false"))

            let target = KurrentDBStateStore (offline, "empty") :> IStateStore
            Expect.isEmpty (target.LoadMany([]).GetAwaiter().GetResult()) "empty load"
            target.SaveMany([]).GetAwaiter().GetResult()
            target.DeleteMany([]).GetAwaiter().GetResult()
        testCase "a new graph and client restore two persisted writes"
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

            write().GetAwaiter().GetResult()
            use second = connect ()
            let restoredStore = KurrentDBStateStore (second, name) :> IStateStore
            let restored = (Persist.loadAsync restoredStore [ key ]).GetAwaiter().GetResult()
            use graph = new Graph ()
            use active = graph.Activate ()
            let count = Persist.signal restored key 0
            let doubled = createMemo (fun _ -> count.Value * 2)
            Expect.equal count.Value 2 "stored signal"
            Expect.equal doubled.Value 4 "derived state recomputed"
        testCase "invalid construction fails before connecting"
        <| fun () ->
            Expect.throwsT<ArgumentNullException>
                (fun () ->
                    KurrentDBStateStore (Unchecked.defaultof<KurrentDBClient>, "app")
                    |> ignore)
                "null client"

            for name in [ ""; " "; String ('x', 513); String (char 0xD800, 1) ] do
                Expect.throws (fun () -> KurrentDBStateStore (client.Value, name) |> ignore) "invalid scope"
    ]

[<Tests>]
let tests =
    testSequenced (
        testList
            "KurrentDB state store"
            [
                RetryTests.tests
                StoreConformance.suite "shared" create
                testList "optimistic" (OptimisticStoreConformance.optimisticStoreCases create)
                testList "KurrentDB" cases
            ]
    )
