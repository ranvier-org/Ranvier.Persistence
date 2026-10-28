module Ranvier.Persistence.Tests.FileStore

open System
open System.IO
open Expecto
open Ranvier.Persistence
open Ranvier.Persistence.Tests.Support

/// <summary>The directory every file-store test of the run works under; it is deleted when the process exits.</summary>
let private runRoot =
    let root =
        Path.Combine (Path.GetTempPath (), "ranvier-persistence-tests", Guid.NewGuid().ToString "N")

    AppDomain.CurrentDomain.ProcessExit.Add (fun _ ->
        try
            Directory.Delete (root, true)
        with _ ->
            ())

    root

/// <summary>A fresh directory under the run's root; it does not exist yet.</summary>
let freshDirectory () =
    Path.Combine (runRoot, Guid.NewGuid().ToString "N")

let private loadMany (store: IStateStore) keys =
    store.LoadMany keys |> Async.AwaitTask

let private saveMany (store: IStateStore) records =
    store.SaveMany records |> Async.AwaitTask

let private contents (records: StoredRecord list) =
    records
    |> List.map (fun record -> record.Key, record.Version, record.Payload)
    |> List.sortBy (fun (key, _, _) -> key)

[<Tests>]
let tests =
    testList
        "FileStateStore"
        [
            StoreConformance.suite "conformance" (fun () -> FileStateStore (freshDirectory ()) :> IStateStore)

            testCaseAsync "a store over a missing directory loads nothing"
            <| async {
                let directory = freshDirectory ()
                let! loaded = loadMany (FileStateStore directory) [ "a" ]
                Expect.isEmpty loaded "no records"
                Expect.isFalse (Directory.Exists directory) "a load creates no directory"
            }

            testCaseAsync "a second store over the directory loads the first store's records"
            <| async {
                let directory = freshDirectory ()
                do! saveMany (FileStateStore directory) [ record "a" 2 "alpha" ]
                let! loaded = loadMany (FileStateStore directory) [ "a" ]
                Expect.equal (contents loaded) [ "a", 2, "alpha" ] "the record is on disk"
            }

            testCaseAsync "a save leaves one file per key and no temporary file"
            <| async {
                let directory = freshDirectory ()
                let store = FileStateStore directory
                do! saveMany store [ record "a" 1 "first"; record "b" 1 "beta" ]
                do! saveMany store [ record "a" 1 "second" ]

                let files =
                    Directory.GetFiles directory
                    |> Array.map Path.GetFileName
                    |> Array.sort

                Expect.equal files.Length 2 "a file per key"
                Expect.all files (fun name -> not (name.EndsWith ".tmp")) "no temporary file remains"
            }

            testCaseAsync "device names and long keys round trip"
            <| async {
                let store = FileStateStore (freshDirectory ())
                let long = String ('k', 400)

                let records =
                    [ record "con" 1 "device"; record "nul.txt" 1 "device"; record long 1 "long" ]

                do! saveMany store records
                let! loaded = loadMany store (records |> List.map _.Key)
                Expect.equal (contents loaded) (contents records) "every record loads unchanged"
            }

            testCaseAsync "an unreadable record file fails the load naming the key"
            <| async {
                let directory = freshDirectory ()
                let store = FileStateStore directory
                do! saveMany store [ record "a" 1 "alpha" ]

                for file in Directory.GetFiles directory do
                    File.WriteAllText (file, "garbage")

                let! error = failureOf (loadMany store [ "a" ])
                Expect.isTrue (error :? InvalidDataException) "a malformed file is invalid data"
                Expect.stringContains error.Message "\"a\"" "the error names the key"
            }
        ]
