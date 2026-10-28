module Ranvier.Persistence.Tests.Collections

open System
open Expecto
open Ranvier
open Ranvier.Persistence
open Ranvier.Persistence.Tests.Support

type private Todo = { Id: int; Text: string }

/// <summary>A <c>Todo</c> codec whose payload is <c>id|text</c>.</summary>
let private todoCodec: PersistCodec<Todo> =
    PersistCodec.ofJournal
        {
            Name = "todo"
            Version = 1
            Encode = fun todo -> string todo.Id + "|" + todo.Text
            Decode =
                fun payload ->
                    match payload.IndexOf '|' with
                    | -1 -> Result.Error ("not a todo: " + payload)
                    | bar ->
                        match Int32.TryParse (payload.Substring (0, bar)) with
                        | true, id ->
                            Result.Ok
                                {
                                    Id = id
                                    Text = payload.Substring (bar + 1)
                                }
                        | _ -> Result.Error ("not a todo id: " + payload)
        }

let private todo id text =
    { Id = id; Text = text }

let private rowTodos = Persist.rows "todos" todoCodec _.Id string
let private documentTodos = Persist.document "todos" todoCodec _.Id

/// <summary>
/// Loads <c>key</c> from <c>store</c>, makes its persisted collection in a fresh graph, runs <c>edit</c> on it, then
/// waits for every save. Returns the rows at the end of <c>edit</c>.
/// </summary>
let private editWith (store: IStateStore) (key: PersistRows<int, Todo>) (edit: KeyedCollection<int, Todo> -> unit) =
    async {
        let! session = Persist.loadAsync store [ key ] |> Async.AwaitTask

        let rows =
            use graph = new Graph (GraphOptions.Default)
            use _active = graph.Activate ()
            let todos = Persist.collection session key
            edit todos

            todos.Rows.Keys
            |> Array.map todos.Rows.Get
            |> List.ofArray

        do! session.FlushAsync () |> Async.AwaitTask
        return rows
    }

/// <summary>
/// A memory store holding <c>todos</c> as saved by a first session over <c>key</c>, and a recording store over it
/// with no saves yet.
/// </summary>
let private seeded (key: PersistRows<int, Todo>) (todos: Todo list) =
    async {
        let inner = MemoryStateStore () :> IStateStore

        let! _ =
            editWith inner key (fun rows ->
                rows.Edit (fun rows ->
                    for value in todos do
                        rows.AddOrUpdate value))

        return inner, RecordingStore inner
    }

let private seed = [ todo 3 "c"; todo 1 "a"; todo 2 "b" ]

let private stored (store: IStateStore) (keys: string list) =
    async {
        let! records = store.LoadMany keys |> Async.AwaitTask

        return
            records
            |> List.map (fun record -> record.Key, record.Payload)
            |> List.sort
    }

let private rowCases =
    testList
        "row layout"
        [
            testCaseAsync "editing one row saves that row's record only"
            <| async {
                let! _, store = seeded rowTodos seed
                let! _ = editWith store rowTodos (fun rows -> rows.AddOrUpdate (todo 1 "a2"))
                Expect.equal (savedPayloads store) [ [ "todos/1", "1|a2" ] ] "one save, one row record"
            }

            testCaseAsync "adding a row saves its record and the order"
            <| async {
                let! _, store = seeded rowTodos seed
                let! _ = editWith store rowTodos (fun rows -> rows.AddOrUpdate (todo 4 "d"))

                Expect.equal
                    (savedPayloads store |> List.map List.sort)
                    [ [ "todos", "1:31:11:21:4"; "todos/4", "4|d" ] ]
                    "one save, the row and the order"
            }

            testCaseAsync "removing a row saves the order and deletes the row's record"
            <| async {
                let! _, store = seeded rowTodos seed
                let! _ = editWith store rowTodos (fun rows -> rows.Remove 1 |> ignore)
                Expect.equal (savedPayloads store) [ [ "todos", "1:31:2" ] ] "one save, the order"
                let! left = stored store [ "todos/1"; "todos/2"; "todos/3" ]
                Expect.equal left [ "todos/2", "2|b"; "todos/3", "3|c" ] "the removed row's record is deleted"
            }

            testCaseAsync "a restored collection keeps insertion order"
            <| async {
                let! _, store = seeded rowTodos seed
                let! rows = editWith store rowTodos ignore
                Expect.equal rows seed "the stored order"
                Expect.isEmpty store.Saves "restoring saves nothing"
            }

            testCaseAsync "a row re-added at the end restores at the end"
            <| async {
                let! _, store = seeded rowTodos seed

                let! _ =
                    editWith store rowTodos (fun rows ->
                        rows.Edit (fun rows ->
                            rows.Remove 3 |> ignore
                            rows.AddOrUpdate (todo 3 "c")))

                let! rows = editWith store rowTodos ignore
                Expect.equal rows [ todo 1 "a"; todo 2 "b"; todo 3 "c" ] "the order after the edit"
            }

            testCaseAsync "an edit in the batch that disposes the collection's owner is saved"
            <| async {
                let! _, store = seeded rowTodos seed

                let! session =
                    Persist.loadAsync store [ rowTodos ]
                    |> Async.AwaitTask

                do
                    use graph = new Graph (GraphOptions.Default)
                    use _active = graph.Activate ()

                    let todos, owner =
                        createRoot (fun (owner: Owner) -> Persist.collection session rowTodos, owner)

                    batch (fun () ->
                        todos.AddOrUpdate (todo 2 "b2")
                        owner.Dispose ())

                do! session.FlushAsync () |> Async.AwaitTask
                Expect.equal (savedPayloads store) [ [ "todos/2", "2|b2" ] ] "the edit is saved"
            }

            testCaseAsync "a collection re-created for the key starts from its last rows"
            <| async {
                let! _, store = seeded rowTodos seed

                let! session =
                    Persist.loadAsync store [ rowTodos ]
                    |> Async.AwaitTask

                use graph = new Graph (GraphOptions.Default)
                use _active = graph.Activate ()

                let todos, owner =
                    createRoot (fun (owner: Owner) -> Persist.collection session rowTodos, owner)

                todos.AddOrUpdate (todo 4 "d")
                owner.Dispose ()
                let again = Persist.collection session rowTodos
                Expect.equal (again.Rows.Keys |> List.ofArray) [ 3; 1; 2; 4 ] "the last rows, in order"
            }

            testCaseAsync "an undecodable row fails the load naming the row's key"
            <| async {
                let! inner, _ = seeded rowTodos seed

                do!
                    inner.SaveMany [ record "todos/2" 1 "garbage" ]
                    |> Async.AwaitTask

                do!
                    Persist.loadAsync inner [ rowTodos ]
                    |> Async.AwaitTask
                    |> expectLoadFailure "todos/2" (DecodeFailed "not a todo: garbage")
            }

            testCaseAsync "with the discard option an undecodable row is left out"
            <| async {
                let! inner, _ = seeded rowTodos seed

                do!
                    inner.SaveMany [ record "todos/2" 1 "garbage" ]
                    |> Async.AwaitTask

                let! session =
                    Persist.loadWith
                        { PersistOptions.defaults with
                            DiscardUndecodable = true
                        }
                        inner
                        [ rowTodos ]
                    |> Async.AwaitTask

                let rows =
                    use graph = new Graph (GraphOptions.Default)
                    use _active = graph.Activate ()
                    let todos = Persist.collection session rowTodos
                    todos.Rows.Keys |> List.ofArray

                Expect.equal rows [ 3; 1 ] "the decodable rows, in order"
            }
        ]

let private documentCases =
    testList
        "document layout"
        [
            testCaseAsync "editing one row saves the one document record"
            <| async {
                let! _, store = seeded documentTodos seed
                let! _ = editWith store documentTodos (fun rows -> rows.AddOrUpdate (todo 1 "a2"))

                Expect.equal (savedPayloads store) [ [ "todos", "3:3|c4:1|a23:2|b" ] ] "one save, one document"
            }

            testCaseAsync "a restored collection keeps insertion order"
            <| async {
                let! _, store = seeded documentTodos seed
                let! rows = editWith store documentTodos (fun rows -> rows.Remove 1 |> ignore)
                Expect.equal rows [ todo 3 "c"; todo 2 "b" ] "the edited rows"
                let! restored = editWith store documentTodos ignore
                Expect.equal restored [ todo 3 "c"; todo 2 "b" ] "the stored order"
            }

            testCaseAsync "an older document runs its migrations on every row"
            <| async {
                let store = MemoryStateStore () :> IStateStore

                do!
                    store.SaveMany [ record "todos" 0 "1:11:2" ]
                    |> Async.AwaitTask

                let migrated =
                    Persist.document
                        "todos"
                        (todoCodec
                         |> PersistCodec.withMigration 0 (fun id -> Result.Ok (id + "|old")))
                        _.Id

                let! rows = editWith store migrated ignore
                Expect.equal rows [ todo 1 "old"; todo 2 "old" ] "each row migrated"
            }
        ]

let private layoutCases =
    testList
        "declaring"
        [
            testCase "a collection key under the reserved prefix is rejected"
            <| fun () ->
                Expect.throwsT<ArgumentException>
                    (fun () ->
                        Persist.rows "$ranvier/todos" todoCodec _.Id string
                        |> ignore)
                    "the reserved prefix"

                Expect.throwsT<ArgumentException>
                    (fun () ->
                        Persist.document "$ranvier/todos" todoCodec _.Id
                        |> ignore)
                    "the reserved prefix"
        ]

[<Tests>]
let tests = testList "Collections" [ rowCases; documentCases; layoutCases ]
