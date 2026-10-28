module Ranvier.Persistence.Tests.Loading

open System
open Expecto
open Ranvier.Persistence
open Ranvier.Persistence.Tests.Support

let private storeWith (records: StoredRecord list) =
    let store = MemoryStateStore ()
    (store :> IStateStore).SaveMany records |> ignore
    store :> IStateStore

let private load options store keys =
    Persist.loadWith options store keys
    |> Async.AwaitTask

/// <summary>A migration that records its version in <c>trail</c> and appends <c>suffix</c>.</summary>
let private appending (trail: ResizeArray<int>) (from: int) (suffix: string) (payload: string) =
    trail.Add from
    Result.Ok (payload + suffix)

let private discarding =
    { PersistOptions.defaults with
        DiscardUndecodable = true
    }

let private textCodec (version: int) : PersistCodec<string> =
    PersistCodec.ofJournal
        {
            Name = "text"
            Version = version
            Encode = id
            Decode = Result.Ok
        }

[<Tests>]
let tests =
    testList
        "Loading"
        [
            testCase "a key under the reserved prefix is rejected"
            <| fun () ->
                Expect.throwsT<ArgumentException>
                    (fun () ->
                        Persist.key "$ranvier/mine" (PersistCodec.ofJournal (intCodec 1))
                        |> ignore)
                    "reserved keys are rejected"

            testCase "an empty key is rejected"
            <| fun () ->
                Expect.throwsT<ArgumentException>
                    (fun () ->
                        Persist.key "" (PersistCodec.ofJournal (intCodec 1))
                        |> ignore)
                    "empty keys are rejected"

            testCaseAsync "two declarations of one key are rejected"
            <| async {
                let first = Persist.key "count" (PersistCodec.ofJournal (intCodec 1))
                let second = Persist.key "count" (PersistCodec.ofJournal (intCodec 1))

                let! error =
                    failureOf (
                        async {
                            return!
                                Persist.loadAsync (MemoryStateStore ()) [ first; second ]
                                |> Async.AwaitTask
                        }
                    )

#if !FABLE_COMPILER
                // Fable raises invalidArg as a plain Error.
                Expect.isTrue (error :? ArgumentException) "a duplicate key is an argument error"
#endif
                Expect.stringContains error.Message "declared more than once" "the error says why"
                Expect.stringContains error.Message "\"count\"" "the error names the key"
            }

            testCaseAsync "a stored value restores and an absent key uses the fallback"
            <| async {
                let count = Persist.key "count" (PersistCodec.ofJournal (intCodec 1))
                let other = Persist.key "other" (PersistCodec.ofJournal (intCodec 1))

                let! session =
                    load PersistOptions.defaults (storeWith [ record "count" 1 "41" ]) [ count; other ]

                Expect.equal (Persist.restore session count 0) 41 "the stored value"
                Expect.equal (Persist.restore session other 7) 7 "the fallback"
            }

            testCaseAsync "an older version runs its migrations in version order"
            <| async {
                let trail = ResizeArray ()

                let codec =
                    textCodec 4
                    |> PersistCodec.withMigration 3 (appending trail 3 "+3")
                    |> PersistCodec.withMigration 1 (appending trail 1 "+1")
                    |> PersistCodec.withMigration 2 (appending trail 2 "+2")

                let key = Persist.key "text" codec

                let! session =
                    load PersistOptions.defaults (storeWith [ record "text" 1 "v1" ]) [ key ]

                Expect.equal (Persist.restore session key "") "v1+1+2+3" "every migration applies"
                Expect.equal (List.ofSeq trail) [ 1; 2; 3 ] "in version order"
            }

            testCaseAsync "a current version runs no migration"
            <| async {
                let trail = ResizeArray ()

                let codec =
                    textCodec 2
                    |> PersistCodec.withMigration 1 (appending trail 1 "+1")

                let key = Persist.key "text" codec

                let! session =
                    load PersistOptions.defaults (storeWith [ record "text" 2 "v2" ]) [ key ]

                Expect.equal (Persist.restore session key "") "v2" "the payload decodes unchanged"
                Expect.isEmpty trail "no migration runs"
            }

            testCaseAsync "a newer version fails the load naming the key"
            <| async {
                let key = Persist.key "count" (PersistCodec.ofJournal (intCodec 2))

                do!
                    load PersistOptions.defaults (storeWith [ record "count" 3 "1" ]) [ key ]
                    |> expectLoadFailure "count" (NewerVersion (3, 2))
            }

            testCaseAsync "a missing migration fails the load naming the key"
            <| async {
                let key =
                    Persist.key
                        "text"
                        (textCodec 3
                         |> PersistCodec.withMigration 2 (fun text -> Result.Ok text))

                do!
                    load PersistOptions.defaults (storeWith [ record "text" 1 "v1" ]) [ key ]
                    |> expectLoadFailure "text" (MissingMigration 1)
            }

            testCaseAsync "a failed migration fails the load naming the key"
            <| async {
                let key =
                    Persist.key
                        "text"
                        (textCodec 2
                         |> PersistCodec.withMigration 1 (fun _ -> Result.Error "bad"))

                do!
                    load PersistOptions.defaults (storeWith [ record "text" 1 "v1" ]) [ key ]
                    |> expectLoadFailure "text" (MigrationFailed (1, "bad"))
            }

            testCaseAsync "a decode failure fails the load naming the key"
            <| async {
                let key = Persist.key "count" (PersistCodec.ofJournal (intCodec 1))

                do!
                    load PersistOptions.defaults (storeWith [ record "count" 1 "many" ]) [ key ]
                    |> expectLoadFailure "count" (DecodeFailed "not an int: many")
            }

            testCaseAsync "with the discard option an undecodable record uses the fallback"
            <| async {
                let count = Persist.key "count" (PersistCodec.ofJournal (intCodec 1))
                let kept = Persist.key "kept" (PersistCodec.ofJournal (intCodec 1))

                let store = storeWith [ record "count" 1 "many"; record "kept" 1 "3" ]

                let! session = load discarding store [ count; kept ]
                Expect.equal (Persist.restore session count -1) -1 "the undecodable record is discarded"
                Expect.equal (Persist.restore session kept -1) 3 "a restorable record is kept"
            }

            testCaseAsync "with the discard option a newer version still fails the load"
            <| async {
                let key = Persist.key "count" (PersistCodec.ofJournal (intCodec 2))

                do!
                    load discarding (storeWith [ record "count" 3 "1" ]) [ key ]
                    |> expectLoadFailure "count" (NewerVersion (3, 2))
            }

            testCaseAsync "with the discard option a missing migration still fails the load"
            <| async {
                let key = Persist.key "text" (textCodec 2)

                do!
                    load discarding (storeWith [ record "text" 1 "v1" ]) [ key ]
                    |> expectLoadFailure "text" (MissingMigration 1)
            }

            testCaseAsync "with the discard option a failed migration still fails the load"
            <| async {
                let key =
                    Persist.key
                        "text"
                        (textCodec 2
                         |> PersistCodec.withMigration 1 (fun _ -> Result.Error "bad"))

                do!
                    load discarding (storeWith [ record "text" 1 "v1" ]) [ key ]
                    |> expectLoadFailure "text" (MigrationFailed (1, "bad"))
            }

            testCaseAsync "a buster mismatch discards every record"
            <| async {
                let count = Persist.key "count" (PersistCodec.ofJournal (intCodec 1))

                let store =
                    storeWith [ record "$ranvier/buster" 1 "a"; record "count" 1 "nonsense" ]

                let! session =
                    load
                        { PersistOptions.defaults with
                            Buster = Some "b"
                        }
                        store
                        [ count ]

                Expect.equal (Persist.restore session count 0) 0 "the record is discarded, not decoded"
            }

            testCaseAsync "a buster set on a store saved without one discards every record"
            <| async {
                let count = Persist.key "count" (PersistCodec.ofJournal (intCodec 1))
                let store = storeWith [ record "count" 1 "5" ]

                let! session =
                    load
                        { PersistOptions.defaults with
                            Buster = Some "b"
                        }
                        store
                        [ count ]

                Expect.equal (Persist.restore session count 0) 0 "the record is discarded"
            }
        ]
