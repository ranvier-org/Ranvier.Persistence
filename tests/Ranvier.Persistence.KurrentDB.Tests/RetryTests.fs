module Ranvier.Persistence.KurrentDB.Tests.RetryTests

open System
open System.Threading.Tasks
open Expecto
open KurrentDB.Client
open Ranvier.Persistence.KurrentDB

let private conflict () =
    WrongExpectedVersionException ("retry-test", StreamState.StreamRevision 0UL, StreamState.StreamRevision 1UL, null, null)

/// <summary>Deterministic revision-conflict retry and failure propagation checks.</summary>
let tests =
    testList
        "snapshot retries"
        [
            testCaseAsync "a stale snapshot becomes visible between append attempts"
            <| async {
                let mutable visible = false
                let mutable calls = 0
                let pauses = ResizeArray<int>()

                let ready =
                    TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

                let pause attempt =
                    pauses.Add attempt
                    ready.Task :> Task

                let operation () =
                    task {
                        calls <- calls + 1

                        if not visible then
                            raise (conflict ())
                    }

                let pending = SnapshotRetry.run pause operation
                Expect.equal calls 1 "an incomplete pause prevents another stale append"
                Expect.isFalse pending.IsCompleted "the operation awaits the pause"
                visible <- true
                ready.SetResult ()
                do! pending |> Async.AwaitTask
                Expect.equal calls 2 "the append retries after the head becomes readable"
                Expect.sequenceEqual pauses [ 1 ] "the conflict waits before reading again"
            }
            testCase "persistent conflicts stop after 32 attempts"
            <| fun () ->
                let mutable calls = 0
                let mutable pauses = 0

                let pause _ =
                    pauses <- pauses + 1
                    Task.CompletedTask

                let operation () =
                    calls <- calls + 1
                    Task.FromException<unit>(conflict ())

                Expect.throwsT<WrongExpectedVersionException>
                    (fun () -> (SnapshotRetry.run pause operation).GetAwaiter().GetResult())
                    "the final conflict propagates"

                Expect.equal calls 32 "the attempt limit is unchanged"
                Expect.equal pauses 31 "the final failed attempt does not wait"
            testCase "unrelated failures propagate without retries"
            <| fun () ->
                let mutable calls = 0
                let mutable pauses = 0
                let failure = InvalidOperationException "offline"

                let pause _ =
                    pauses <- pauses + 1
                    Task.CompletedTask

                let operation () =
                    calls <- calls + 1
                    Task.FromException<unit> failure

                let mutable caught = Unchecked.defaultof<exn>

                try
                    (SnapshotRetry.run pause operation).GetAwaiter().GetResult()
                with error ->
                    caught <- error

                Expect.isTrue (obj.ReferenceEquals (caught, failure)) "the original failure propagates"
                Expect.equal calls 1 "unrelated failures do not retry"
                Expect.equal pauses 0 "unrelated failures do not wait"
        ]
