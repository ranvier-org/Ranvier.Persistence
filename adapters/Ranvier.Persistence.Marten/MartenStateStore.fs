namespace Ranvier.Persistence.Marten

open System
open System.Collections.Generic
open System.Text
open System.Threading.Tasks
open JasperFx.Metadata
open Marten
open Marten.Schema
open Marten.Services
open Ranvier.Persistence
open Weasel.Storage

/// <summary>Marten's document representation of a persisted input, partitioned by application scope.</summary>
/// <remarks>Applications use <c>MartenStateStore</c>; altering this mapping changes its storage format.</remarks>
[<CLIMutable; MultiTenanted>]
type MartenStateDocument =
    {
        Id: string
        Key: string
        CodecVersion: int
        Payload: string
        mutable Version: Guid
    }

    interface IVersioned with
        member this.Version
            with get () = this.Version
            and set value = this.Version <- value

/// <summary>Stores inputs in a Marten tenant. A supplied ETag requires the current version; <c>None</c> overwrites.</summary>
/// <remarks>The caller owns <c>database</c>. A nonblank <c>scope</c> isolates records, including <c>Clear</c>.</remarks>
type MartenStateStore(database: IDocumentStore, scope: string) =
    do
        if isNull database then
            nullArg (nameof database)

        if String.IsNullOrWhiteSpace scope then
            invalidArg (nameof scope) "An application scope is required."

    let utf8 = UTF8Encoding (false, true)

    let identity (key: string) =
        if isNull key then
            nullArg (nameof key)

        "k" + Convert.ToBase64String (utf8.GetBytes key)

    let prepare (records: StoredRecord list) =
        let seen = HashSet<string>(StringComparer.Ordinal)

        records
        |> List.map (fun record ->
            if not (seen.Add record.Key) then
                invalidArg
                    (nameof records)
                    ("A save contains the key more than once: "
                     + record.Key)

            let version =
                match record.ETag with
                | None -> Guid.Empty
                | Some tag ->
                    match Guid.TryParse tag with
                    | true, version -> version
                    | _ -> invalidArg (nameof records) ("Invalid ETag for key: " + record.Key)

            record.ETag.IsSome,
            {
                Id = identity record.Key
                Key = record.Key
                CodecVersion = record.Version
                Payload = record.Payload
                Version = version
            })

    let session () =
        database.LightweightSession (SessionOptions (TenantId = scope, ConcurrencyChecks = ConcurrencyChecks.Disabled))

    interface IStateStore with
        member _.LoadMany keys =
            task {
                let ids =
                    keys
                    |> List.map identity
                    |> List.distinct
                    |> List.toArray

                if ids.Length = 0 then
                    return []
                else
                    use query = database.QuerySession (scope)
                    let! loaded = query.LoadManyAsync<MartenStateDocument>(ids)

                    return
                        loaded
                        |> Seq.map (fun row ->
                            {
                                Key = row.Key
                                Version = row.CodecVersion
                                Payload = row.Payload
                                ETag = Some (row.Version.ToString ("D"))
                            })
                        |> List.ofSeq
            }

        member _.SaveMany records =
            task {
                let rows = prepare records

                if not rows.IsEmpty then
                    use work = session ()

                    for conditional, row in rows do
                        if conditional then
                            // Update replaces the queued upsert, so a deleted record fails instead of being recreated.
                            work.UpdateExpectedVersion (row, row.Version)
                            work.Update row
                        else
                            work.Store row

                    do! work.SaveChangesAsync ()
            }
            :> Task

        member _.DeleteMany keys =
            task {
                let ids = keys |> List.map identity |> List.distinct

                if not ids.IsEmpty then
                    use work = session ()

                    for id in ids do
                        work.Delete<MartenStateDocument>(id)

                    do! work.SaveChangesAsync ()
            }
            :> Task

        member _.Clear() =
            task {
                use work = session ()
                work.DeleteWhere<MartenStateDocument>(fun _ -> true)
                do! work.SaveChangesAsync ()
            }
            :> Task
