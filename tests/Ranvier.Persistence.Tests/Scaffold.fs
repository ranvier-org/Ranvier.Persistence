module Ranvier.Persistence.Tests.Scaffold

open Expecto
open Ranvier.Persistence

[<Tests>]
let tests =
    testList
        "Scaffold"
        [
            testCase "a load exception names the key"
            <| fun () ->
                let error = PersistLoadException ("theme", NewerVersion (3, 2))
                Expect.stringContains error.Message "\"theme\"" "the message names the key"
                Expect.equal error.Failure (NewerVersion (3, 2)) "the failure is kept"
        ]
