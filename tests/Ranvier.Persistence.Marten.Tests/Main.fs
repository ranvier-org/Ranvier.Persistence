module Ranvier.Persistence.Marten.Tests.Main

[<EntryPoint>]
let main args =
    Expecto.Tests.runTestsWithCLIArgs [] args Tests.tests
