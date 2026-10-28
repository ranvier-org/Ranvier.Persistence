/// <summary>The part of Expecto the suite uses, over Fable.Mocha, so its files compile unchanged under Fable.</summary>
[<AutoOpen>]
module Expecto

open System
open Fable.Mocha

type Test = TestCase

/// <summary>Marks a suite's root value. The Fable run lists its roots in <c>Main.fs</c>.</summary>
[<AttributeUsage(AttributeTargets.Property
                 ||| AttributeTargets.Method)>]
type TestsAttribute() =
    inherit Attribute()

let testList (name: string) (tests: Test list) : Test =
    Test.testList name tests

let testSequenced (t: Test) : Test = t

let testCase (name: string) (body: unit -> unit) : Test =
    Test.testCase name body

let testCaseAsync (name: string) (body: Async<unit>) : Test =
    Test.testCaseAsync name body

let inline test (name: string) =
    Test.test name

let failtest (message: string) : 'a =
    failwith message

let failtestf format =
    Printf.ksprintf failwith format

let skiptest (message: string) : 'a =
    failwith ("skipped: " + message)

[<RequireQualifiedAccess>]
module Expect =
    let equal (actual: 'a) (expected: 'a) (message: string) =
        if actual <> expected then
            failwithf "%s.\nExpected: %A\nActual:   %A" message expected actual

    let notEqual (actual: 'a) (expected: 'a) (message: string) =
        if actual = expected then
            failwithf "%s. Expected a value other than %A" message expected

    let isTrue (actual: bool) (message: string) =
        if not actual then
            failwithf "%s. Expected true" message

    let isFalse (actual: bool) (message: string) =
        if actual then
            failwithf "%s. Expected false" message

    let isNull (actual: 'a) (message: string) =
        if not (obj.ReferenceEquals (actual, null)) then
            failwithf "%s. Expected null" message

    let isNone (actual: 'a option) (message: string) =
        if actual.IsSome then
            failwithf "%s. Expected None, was %A" message actual

    let sequenceEqual (actual: 'a seq) (expected: 'a seq) (message: string) =
        let actual = List.ofSeq actual
        let expected = List.ofSeq expected

        if actual <> expected then
            failwithf "%s.\nExpected: %A\nActual:   %A" message expected actual

    let isEmpty (actual: 'a seq) (message: string) =
        if not (Seq.isEmpty actual) then
            failwithf "%s. Expected empty, was %A" message (List.ofSeq actual)

    let isNonEmpty (actual: 'a seq) (message: string) =
        if Seq.isEmpty actual then
            failwithf "%s. Expected a non-empty sequence" message

    let contains (actual: 'a seq) (element: 'a) (message: string) =
        if not (Seq.contains element actual) then
            failwithf "%s. %A does not contain %A" message (List.ofSeq actual) element

    let containsAll (actual: 'a seq) (expected: 'a seq) (message: string) =
        let missing =
            expected
            |> Seq.filter (fun e -> not (Seq.contains e actual))
            |> List.ofSeq

        if not missing.IsEmpty then
            failwithf "%s. Missing %A" message missing

    let exists (actual: 'a seq) (predicate: 'a -> bool) (message: string) =
        if not (Seq.exists predicate actual) then
            failwithf "%s. No element matched" message

    let all (actual: 'a seq) (predicate: 'a -> bool) (message: string) =
        if not (Seq.forall predicate actual) then
            failwithf "%s. An element failed" message

    let stringContains (actual: string) (part: string) (message: string) =
        if not (actual.Contains part) then
            failwithf "%s. %A does not contain %A" message actual part

    let stringStarts (actual: string) (start: string) (message: string) =
        if not (actual.StartsWith start) then
            failwithf "%s. %A does not start with %A" message actual start

    let isLessThan (actual: 'a) (bound: 'a) (message: string) =
        if not (actual < bound) then
            failwithf "%s. Expected %A < %A" message actual bound

    let isLessThanOrEqual (actual: 'a) (bound: 'a) (message: string) =
        if not (actual <= bound) then
            failwithf "%s. Expected %A <= %A" message actual bound

    let isGreaterThan (actual: 'a) (bound: 'a) (message: string) =
        if not (actual > bound) then
            failwithf "%s. Expected %A > %A" message actual bound

    /// <summary>The exception <c>f</c> raises, if any.</summary>
    let caught (f: unit -> unit) : exn option =
        try
            f ()
            None
        with e ->
            Some e

    let throws (f: unit -> unit) (message: string) =
        if (caught f).IsNone then
            failwithf "%s. Expected an exception" message

    let throwsC (f: unit -> unit) (cont: exn -> 'a) : 'a =
        match caught f with
        | Some e -> cont e
        | None -> failwith "Expected an exception"

    /// <summary>
    /// Under Fable a <c>System</c> exception type does not survive as a type: <c>invalidOp</c>, <c>nullArg</c> and
    /// the collections raise plain errors. An expectation of one accepts any exception.
    /// </summary>
    let inline throwsT<'e when 'e :> exn> (f: unit -> unit) (message: string) =
        match caught f with
        | Some (:? 'e) -> ()
        | Some _ when typeof<'e>.FullName.StartsWith "System." -> ()
        | Some e -> failwithf "%s. Expected %s, got: %s" message typeof<'e>.Name e.Message
        | None -> failwithf "%s. Expected %s" message typeof<'e>.Name
