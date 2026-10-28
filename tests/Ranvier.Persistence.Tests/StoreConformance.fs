/// <summary>
/// The conformance suite every <c>IStateStore</c> passes. A store's tests call <c>StoreConformance.suite</c> with a
/// factory that returns an empty store on each call.
/// </summary>
module Ranvier.Persistence.Tests.StoreConformance

open Expecto
open Ranvier.Persistence

let private record = Support.record

/// <summary>The key, version and payload of each record, ordered by key.</summary>
let private contents (records: StoredRecord list) =
    records
    |> List.map (fun record -> record.Key, record.Version, record.Payload)
    |> List.sortBy (fun (key, _, _) -> key)

let private loadMany (store: IStateStore) keys =
    store.LoadMany keys |> Async.AwaitTask

let private saveMany (store: IStateStore) records =
    store.SaveMany records |> Async.AwaitTask

let private deleteMany (store: IStateStore) keys =
    store.DeleteMany keys |> Async.AwaitTask

/// <summary>The load, save and delete cases, run against stores made by <c>create</c>.</summary>
let storeCases (create: unit -> IStateStore) =
    [
        testCaseAsync "an empty store loads no records"
        <| async {
            let store = create ()
            let! loaded = loadMany store [ "a"; "b" ]
            Expect.isEmpty loaded "absent keys load nothing"
        }

        testCaseAsync "saved records load back"
        <| async {
            let store = create ()
            do! saveMany store [ record "a" 1 "alpha"; record "b" 2 "beta" ]
            let! loaded = loadMany store [ "a"; "b" ]
            Expect.equal (contents loaded) [ "a", 1, "alpha"; "b", 2, "beta" ] "both records load"
        }

        testCaseAsync "a load returns the requested keys only"
        <| async {
            let store = create ()
            do! saveMany store [ record "a" 1 "alpha"; record "b" 1 "beta" ]
            let! loaded = loadMany store [ "b"; "missing" ]
            Expect.equal (contents loaded) [ "b", 1, "beta" ] "only the present requested key loads"
        }

        testCaseAsync "a later save of a key replaces its record"
        <| async {
            let store = create ()
            do! saveMany store [ record "a" 1 "first" ]
            do! saveMany store [ record "a" 2 "second" ]
            let! loaded = loadMany store [ "a" ]
            Expect.equal (contents loaded) [ "a", 2, "second" ] "the last save wins"
        }

        testCaseAsync "deleted keys load nothing and absent keys are skipped"
        <| async {
            let store = create ()
            do! saveMany store [ record "a" 1 "alpha"; record "b" 1 "beta" ]
            do! deleteMany store [ "a"; "missing" ]
            let! loaded = loadMany store [ "a"; "b"; "missing" ]
            Expect.equal (contents loaded) [ "b", 1, "beta" ] "only the kept record loads"
        }

        testCaseAsync "a cleared store loads no records and takes later saves"
        <| async {
            let store = create ()
            do! saveMany store [ record "a" 1 "alpha"; record "$ranvier/buster" 1 "v1" ]
            do! store.Clear () |> Async.AwaitTask
            do! store.Clear () |> Async.AwaitTask
            let! cleared = loadMany store [ "a"; "$ranvier/buster" ]
            Expect.isEmpty cleared "every record is removed"
            do! saveMany store [ record "b" 1 "beta" ]
            let! loaded = loadMany store [ "a"; "b" ]
            Expect.equal (contents loaded) [ "b", 1, "beta" ] "a save after the clear loads"
        }

        testCaseAsync "keys and payloads with separators, reserved prefixes and non-ASCII text round trip"
        <| async {
            let store = create ()

            let records =
                [
                    record "$ranvier/buster" 1 "v2"
                    record "todos/42" 1 "{\"title\":\"a/b\\\\c\"}"
                    record "Ünïcode: key?*<>|" 3 "line one\nline two\té"
                    record "TODOS/42" 1 "upper"
                ]

            do! saveMany store records
            let! loaded = loadMany store (records |> List.map _.Key)
            Expect.equal (contents loaded) (contents records) "every record loads unchanged"
        }
    ]

let private countKey (version: int) =
    Persist.key "count" (PersistCodec.ofJournal (Support.intCodec version))

/// <summary>The version and buster cases: records saved to a store made by <c>create</c>, loaded by <c>Persist</c>.</summary>
let persistCases (create: unit -> IStateStore) =
    [
        testCaseAsync "a record of an older version loads through its migrations"
        <| async {
            let store = create ()
            do! saveMany store [ record "count" 1 "20" ]

            let key =
                Persist.key
                    "count"
                    (PersistCodec.ofJournal (Support.intCodec 2)
                     |> PersistCodec.withMigration 1 (fun text -> Result.Ok (string (int text * 2))))

            let! session = Persist.loadAsync store [ key ] |> Async.AwaitTask
            Expect.equal (Persist.restore session key 0) 40 "the migrated value"
        }

        testCaseAsync "a record of a newer version fails the load naming the key"
        <| async {
            let store = create ()
            do! saveMany store [ record "count" 3 "1" ]

            do!
                Persist.loadAsync store [ countKey 2 ]
                |> Async.AwaitTask
                |> Support.expectLoadFailure "count" (NewerVersion (3, 2))
        }

        testCaseAsync "a matching buster keeps the records and a different one discards them"
        <| async {
            let store = create ()
            do! saveMany store [ record "$ranvier/buster" 1 "a"; record "count" 1 "9" ]
            let key = countKey 1

            let! matching =
                Persist.loadWith
                    { PersistOptions.defaults with
                        Buster = Some "a"
                    }
                    store
                    [ key ]
                |> Async.AwaitTask

            let! different =
                Persist.loadWith
                    { PersistOptions.defaults with
                        Buster = Some "b"
                    }
                    store
                    [ key ]
                |> Async.AwaitTask

            Expect.equal (Persist.restore matching key 0) 9 "the matching buster keeps the record"
            Expect.equal (Persist.restore different key 0) 0 "a different buster discards it"
        }
    ]

/// <summary>The store conformance cases under the store's name.</summary>
let suite (name: string) (create: unit -> IStateStore) =
    testList name (storeCases create @ persistCases create)
