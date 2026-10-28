module Ranvier.Persistence.AotSmoke.Program

open System

[<EntryPoint>]
let main _ =
    let mutable failures = 0

    for name, ok, detail in PersistSmoke.checks () do
        if ok then
            Console.WriteLine ("ok   " + name)
        else
            failures <- failures + 1
            Console.WriteLine ("FAIL " + name + ": " + detail)

    if failures = 0 then
        Console.WriteLine "all checks passed"
        0
    else
        Console.WriteLine (string failures + " checks failed")
        1
