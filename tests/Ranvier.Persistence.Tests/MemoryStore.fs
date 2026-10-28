module Ranvier.Persistence.Tests.MemoryStore

open Expecto
open Ranvier.Persistence

[<Tests>]
let tests =
    StoreConformance.suite "MemoryStateStore" (fun () -> MemoryStateStore () :> IStateStore)
