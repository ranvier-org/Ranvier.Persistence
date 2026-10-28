module Ranvier.Persistence.Equinox.Tests.Main

open Expecto

[<EntryPoint>]
let main args =
    Tests.runTestsInAssemblyWithCLIArgs [] args
