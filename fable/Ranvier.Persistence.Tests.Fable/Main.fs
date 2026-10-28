module Ranvier.Persistence.Tests.Fable.Main

open Fable.Core
open Fable.Mocha

/// <summary>
/// Sets how a test's <c>TaskCompletionSource</c> delivers: <c>"inline"</c> or <c>"promise"</c>. The sample is a
/// <c>TaskCompletionSource</c> of the compiled Fable library.
/// </summary>
[<Import("install", "./Delivery.js")>]
let private install (mode: string) (source: obj) : string = jsNative

[<Emit("process.env.RANVIER_FABLE_DELIVERY ?? 'promise'")>]
let private requested: string = jsNative

install requested (System.Threading.Tasks.TaskCompletionSource<unit>())
|> ignore

// Every [<Tests>] value of tests/Ranvier.Persistence.Tests that compiles under Fable, under the name of its file.
let all =
    testList
        "Ranvier.Persistence"
        [
            testList "Scaffold.fs" [ Ranvier.Persistence.Tests.Scaffold.tests ]
            testList "MemoryStore.fs" [ Ranvier.Persistence.Tests.MemoryStore.tests ]
            testList "Loading.fs" [ Ranvier.Persistence.Tests.Loading.tests ]
            testList "Coalescing.fs" [ Ranvier.Persistence.Tests.Coalescing.tests ]
            testList "Durability.fs" [ Ranvier.Persistence.Tests.Durability.tests ]
            testList "Collections.fs" [ Ranvier.Persistence.Tests.Collections.tests ]
            testList "Position.fs" [ Ranvier.Persistence.Tests.Position.tests ]
            testList "LocalStorage.fs" [ Ranvier.Persistence.Tests.LocalStorage.tests ]
        ]

Mocha.runTests all |> ignore
