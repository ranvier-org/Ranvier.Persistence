namespace Ranvier.Persistence

open System.Collections.Generic
open System.Threading.Tasks

/// <summary>An <c>IStateStore</c> in process memory. Every task it returns has finished.</summary>
type MemoryStateStore() =
    let records = Dictionary<string, StoredRecord>()

    /// <summary>The stored records, in any order.</summary>
    member _.Records: StoredRecord list = records.Values |> List.ofSeq

    interface IStateStore with
        member _.LoadMany(keys: string list) =
            keys
            |> List.distinct
            |> List.choose (fun key ->
                match records.TryGetValue key with
                | true, record -> Some record
                | _ -> None)
            |> Task.FromResult

        member _.SaveMany(saved: StoredRecord list) =
            for record in saved do
                records[record.Key] <- record

            PersistTask.completed ()

        member _.DeleteMany(keys: string list) =
            for key in keys do
                records.Remove key |> ignore

            PersistTask.completed ()

        member _.Clear() =
            records.Clear ()
            PersistTask.completed ()
