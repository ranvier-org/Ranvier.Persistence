namespace Ranvier.Persistence

#if FABLE_COMPILER
open System
open System.Threading.Tasks
open Fable.Core

/// <summary>The Web Storage operations <c>LocalStorageStateStore</c> uses. A browser's <c>localStorage</c> implements them.</summary>
type IWebStorage =
    /// <summary>The number of stored items.</summary>
    abstract length: int
    /// <summary>The key of the item at <c>index</c>, or <c>null</c> past the last item.</summary>
    abstract key: index: int -> string
    /// <summary>The value stored under <c>key</c>, or <c>null</c> when absent.</summary>
    abstract getItem: key: string -> string
    /// <summary>Stores <c>value</c> under <c>key</c>; raises when the storage is full.</summary>
    abstract setItem: key: string * value: string -> unit
    /// <summary>Removes the item under <c>key</c>; an absent key is skipped.</summary>
    abstract removeItem: key: string -> unit

/// <summary>The item format of <c>LocalStorageStateStore</c>: the record's version, a colon, then its payload.</summary>
module internal LocalStorageItem =
    [<Emit("globalThis.localStorage")>]
    let browserStorage: IWebStorage = jsNative

    [<Emit("Promise.reject($0)")>]
    let failed (error: exn) : Task = jsNative

    [<Emit("Promise.reject($0)")>]
    let failedWith<'T> (error: exn) : Task<'T> = jsNative

    let encode (record: StoredRecord) =
        string record.Version + ":" + record.Payload

    let decode (key: string) (item: string) : StoredRecord =
        let colon = item.IndexOf ':'

        match
            if colon < 0 then
                None
            else
                match Int32.TryParse (item.Substring (0, colon)) with
                | true, version -> Some version
                | _ -> None
        with
        | Some version ->
            {
                Key = key
                Version = version
                Payload = item.Substring (colon + 1)
                ETag = None
            }
        | None ->
            raise (
                FormatException (
                    "The stored item of the key \""
                    + key
                    + "\" is not a persisted record."
                )
            )

/// <summary>
/// An <c>IStateStore</c> over Web Storage: each record is an item under <c>prefix + key</c>. <c>storage</c> defaults
/// to the browser's <c>localStorage</c>. <c>Clear</c> removes the items under the prefix only. Every task it returns
/// has settled; a failed storage call, such as a full storage, faults it.
/// </summary>
type LocalStorageStateStore(prefix: string, ?storage: IWebStorage) =
    let storage = defaultArg storage LocalStorageItem.browserStorage

    let attempt (work: unit -> unit) : Task =
        try
            work ()
            PersistTask.completed ()
        with error ->
            LocalStorageItem.failed error

    /// <summary>The prefix of the store's item keys.</summary>
    member _.Prefix = prefix

    interface IStateStore with
        member _.LoadMany(keys: string list) =
            try
                keys
                |> List.distinct
                |> List.choose (fun key ->
                    match storage.getItem (prefix + key) with
                    | null -> None
                    | item -> Some (LocalStorageItem.decode key item))
                |> PersistTask.result
            with error ->
                LocalStorageItem.failedWith error

        member _.SaveMany(records: StoredRecord list) =
            attempt (fun () ->
                for record in records do
                    storage.setItem (prefix + record.Key, LocalStorageItem.encode record))

        member _.DeleteMany(keys: string list) =
            attempt (fun () ->
                for key in keys do
                    storage.removeItem (prefix + key))

        member _.Clear() =
            attempt (fun () ->
                let owned =
                    [
                        for index in 0 .. storage.length - 1 do
                            match storage.key index with
                            | null -> ()
                            | key when key.StartsWith (prefix, StringComparison.Ordinal) -> yield key
                            | _ -> ()
                    ]

                for key in owned do
                    storage.removeItem key)
#endif
