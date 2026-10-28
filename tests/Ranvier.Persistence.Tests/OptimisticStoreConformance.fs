/// <summary>Conditional-save cases for adapters that issue ETags; the factory returns an empty store.</summary>
module Ranvier.Persistence.Tests.OptimisticStoreConformance

open Expecto
open Ranvier.Persistence

let optimisticStoreCases (create: unit -> IStateStore) =
    let record payload =
        {
            Key = "count"
            Version = 1
            Payload = payload
            ETag = None
        }

    [
        testCaseAsync "load exposes a backend ETag"
        <| async {
            let store = create ()
            do! store.SaveMany [ record "1" ] |> Async.AwaitTask
            let! loaded = store.LoadMany [ "count" ] |> Async.AwaitTask
            Expect.isSome loaded.Head.ETag "a saved record has a token"
        }
        testCaseAsync "a conditional save rotates its ETag and rejects a stale writer"
        <| async {
            let store = create ()
            do! store.SaveMany [ record "1" ] |> Async.AwaitTask
            let! loaded = store.LoadMany [ "count" ] |> Async.AwaitTask
            let stale = loaded.Head

            do!
                store.SaveMany [ { stale with Payload = "2" } ]
                |> Async.AwaitTask

            let! updated = store.LoadMany [ "count" ] |> Async.AwaitTask
            Expect.isSome updated.Head.ETag "the new version has a token"
            Expect.notEqual updated.Head.ETag stale.ETag "the token changes"

            let! error =
                store.SaveMany [ { stale with Payload = "3" } ]
                |> Async.AwaitTask
                |> Async.Catch

            match error with
            | Choice1Of2 _ -> failtest "a stale update succeeded"
            | Choice2Of2 _ -> ()

            let! kept = store.LoadMany [ "count" ] |> Async.AwaitTask
            Expect.equal kept.Head.Payload "2" "the accepted write remains"
        }
        testCaseAsync "a conditional save cannot resurrect a deleted record"
        <| async {
            let store = create ()
            do! store.SaveMany [ record "1" ] |> Async.AwaitTask
            let! loaded = store.LoadMany [ "count" ] |> Async.AwaitTask
            do! store.DeleteMany [ "count" ] |> Async.AwaitTask

            let! error =
                store.SaveMany [ { loaded.Head with Payload = "2" } ]
                |> Async.AwaitTask
                |> Async.Catch

            match error with
            | Choice1Of2 _ -> failtest "a deleted record was recreated with its old token"
            | Choice2Of2 _ -> ()

            let! missing = store.LoadMany [ "count" ] |> Async.AwaitTask
            Expect.isEmpty missing "the deleted record stays absent"
        }
    ]
