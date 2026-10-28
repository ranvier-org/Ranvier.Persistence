namespace Ranvier.Persistence

open System
open System.Collections.Generic
open System.Threading.Tasks
open Ranvier

/// <summary>Building and applying persist codecs.</summary>
module PersistCodec =
    /// <summary>The journal codec with no migrations.</summary>
    let ofJournal (codec: JournalCodec<'T>) : PersistCodec<'T> =
        { Codec = codec; Migrations = [] }

    /// <summary>
    /// Adds the migration from version <c>from</c> to <c>from + 1</c>. Migrations apply in version order, whatever
    /// order they are added in.
    /// </summary>
    let withMigration (from: int) (migrate: string -> Result<string, string>) (codec: PersistCodec<'T>) =
        { codec with
            Migrations =
                codec.Migrations
                @ [ { From = from; Migrate = migrate } ]
        }

    /// <summary>
    /// The value of <c>record</c>: its payload brought to the codec's version by the migrations, then decoded.
    /// </summary>
    let restore (codec: PersistCodec<'T>) (record: StoredRecord) : Result<'T, PersistFailure> =
        let target = codec.Codec.Version

        let rec migrate (version: int) (payload: string) =
            if version = target then
                Result.Ok payload
            else
                match
                    codec.Migrations
                    |> List.tryFind (fun migration -> migration.From = version)
                with
                | None -> Result.Error (MissingMigration version)
                | Some migration ->
                    match migration.Migrate payload with
                    | Result.Ok next -> migrate (version + 1) next
                    | Result.Error message -> Result.Error (MigrationFailed (version, message))

        if record.Version > target then
            Result.Error (NewerVersion (record.Version, target))
        else
            migrate record.Version record.Payload
            |> Result.bind (fun payload ->
                codec.Codec.Decode payload
                |> Result.mapError DecodeFailed)

    /// <summary>The record of <c>value</c> under <c>key</c>, at the codec's version.</summary>
    let encode (codec: PersistCodec<'T>) (key: string) (value: 'T) : StoredRecord =
        {
            Key = key
            Version = codec.Codec.Version
            Payload = codec.Codec.Encode value
            ETag = None
        }

/// <summary>A list of texts in one payload: each text as its length, a colon, then the text.</summary>
module internal PersistFrames =
    /// <summary>The version of a row index record.</summary>
    [<Literal>]
    let IndexVersion = 1

    let encode (texts: string seq) =
        texts
        |> Seq.map (fun text -> string text.Length + ":" + text)
        |> String.concat ""

    let decode (payload: string) : Result<string list, string> =
        let texts = ResizeArray<string>()
        let mutable index = 0
        let mutable failure = None

        while failure.IsNone && index < payload.Length do
            let colon = payload.IndexOf (':', index)

            let length =
                if colon < 0 then
                    -1
                else
                    match Int32.TryParse (payload.Substring (index, colon - index)) with
                    | true, length when
                        length >= 0
                        && colon + 1 + length <= payload.Length
                        ->
                        length
                    | _ -> -1

            if length < 0 then
                failure <- Some ("malformed frame at offset " + string index)
            else
                texts.Add (payload.Substring (colon + 1, length))
                index <- colon + 1 + length

        match failure with
        | Some message -> Result.Error message
        | None -> Result.Ok (List.ofSeq texts)

/// <summary>How <c>Persist.loadAsync</c> restores a declared key. Built by <c>Persist.key</c>, <c>Persist.rows</c> and <c>Persist.document</c>.</summary>
[<Sealed>]
type PersistLoader
    internal
    (
        rowKeys: StoredRecord -> Result<string list, PersistFailure>,
        restore: bool -> StoredRecord -> (string -> StoredRecord option) -> Result<obj, string * PersistFailure>
    ) =
    /// <summary>The store keys of the row records the key's stored record lists, loaded after it.</summary>
    member internal _.RowKeys = rowKeys

    /// <summary>
    /// The boxed value of the stored record and its row records, or the store key of the failing record and the
    /// failure. With the discard flag set, an undecodable row is left out.
    /// </summary>
    member internal _.Restore = restore

/// <summary>A declared persisted key, as <c>Persist.loadAsync</c> reads it.</summary>
type IPersistKey =
    /// <summary>The store key.</summary>
    abstract Key: string
    /// <summary>How the key's stored records are restored.</summary>
    abstract Loader: PersistLoader

/// <summary>A persisted value's store key and codec. <c>Persist.key</c> declares one.</summary>
type PersistKey<'T> internal (key: string, codec: PersistCodec<'T>) =
    let loader =
        PersistLoader (
            (fun _ -> Result.Ok []),
            fun _ record _ ->
                PersistCodec.restore codec record
                |> Result.map box
                |> Result.mapError (fun failure -> key, failure)
        )

    /// <summary>The store key.</summary>
    member _.Key = key

    /// <summary>The codec of the stored value.</summary>
    member _.Codec = codec

    interface IPersistKey with
        member _.Key = key
        member _.Loader = loader

/// <summary>
/// A persisted async source's store key and value codec. <c>Persist.sourceKey</c> declares one.
/// </summary>
/// <remarks>
/// The record's payload holds two frames: <c>ready</c> and the codec's payload, or <c>failed</c> and the failure's
/// message. The record's version is the codec's, and the codec's migrations apply to the value frame.
/// </remarks>
[<Sealed>]
type PersistSourceKey<'T> internal (key: string, codec: PersistCodec<'T>) =
    let loader =
        PersistLoader (
            (fun _ -> Result.Ok []),
            fun _ record _ ->
                let restored =
                    if record.Version > codec.Codec.Version then
                        Result.Error (NewerVersion (record.Version, codec.Codec.Version))
                    else
                        match PersistFrames.decode record.Payload with
                        | Result.Error message -> Result.Error (DecodeFailed message)
                        | Result.Ok [ "ready"; payload ] ->
                            PersistCodec.restore codec { record with Payload = payload }
                            |> Result.map (fun value -> box (Reading<'T>.Ready value))
                        | Result.Ok [ "failed"; message ] -> Result.Ok (box (Reading<'T>.Failed(PersistedFailure message)))
                        | Result.Ok _ -> Result.Error (DecodeFailed "a source record holds the frames ready and a value, or failed and a message")

                restored
                |> Result.mapError (fun failure -> key, failure)
        )

    /// <summary>The store key.</summary>
    member _.Key = key

    /// <summary>The codec of the settled value.</summary>
    member _.Codec = codec

    interface IPersistKey with
        member _.Key = key
        member _.Loader = loader

/// <summary>The records a persisted collection is stored as.</summary>
type internal RowLayout<'K> =
    /// <summary>A record per row under <c>key/keyText rowKey</c>, and the row key texts in order under <c>key</c>.</summary>
    | Rows of keyText: ('K -> string)
    /// <summary>Every row payload in order, in one record under <c>key</c>.</summary>
    | Document

/// <summary>
/// A persisted keyed collection's store key, row codec and layout. <c>Persist.rows</c> and <c>Persist.document</c>
/// declare one.
/// </summary>
[<Sealed>]
type PersistRows<'K, 'V when 'K: equality> internal (key: string, codec: PersistCodec<'V>, keyOf: 'V -> 'K, layout: RowLayout<'K>) =
    let withPayload (record: StoredRecord) payload =
        { record with Payload = payload }

    // Restores each row record in order; an undecodable row is left out when discarding.
    let restoreRows (discard: bool) (records: (string * StoredRecord) list) =
        let values = ResizeArray<'V>()
        let mutable failure = None

        for name, record in records do
            if failure.IsNone then
                match PersistCodec.restore codec record with
                | Result.Ok value -> values.Add value
                | Result.Error (DecodeFailed _) when discard -> ()
                | Result.Error reason -> failure <- Some (name, reason)

        match failure with
        | Some failure -> Result.Error failure
        | None -> Result.Ok (box (List.ofSeq values))

    let frames (record: StoredRecord) =
        PersistFrames.decode record.Payload
        |> Result.mapError DecodeFailed

    let loader =
        match layout with
        | Rows _ ->
            let index (record: StoredRecord) =
                if record.Version > PersistFrames.IndexVersion then
                    Result.Error (NewerVersion (record.Version, PersistFrames.IndexVersion))
                else
                    frames record
                    |> Result.map (List.map (fun text -> key + "/" + text))

            PersistLoader (
                index,
                fun discard record lookup ->
                    index record
                    |> Result.mapError (fun failure -> key, failure)
                    |> Result.bind (fun names ->
                        names
                        |> List.choose (fun name -> lookup name |> Option.map (fun row -> name, row))
                        |> restoreRows discard)
            )
        | Document ->
            PersistLoader (
                (fun _ -> Result.Ok []),
                fun discard record _ ->
                    frames record
                    |> Result.mapError (fun failure -> key, failure)
                    |> Result.bind (fun payloads ->
                        payloads
                        |> List.map (fun payload -> key, withPayload record payload)
                        |> restoreRows discard)
            )

    /// <summary>The store key of the collection's index or document record.</summary>
    member _.Key = key

    /// <summary>The codec of a row.</summary>
    member _.Codec = codec

    member internal _.KeyOf = keyOf
    member internal _.Layout = layout

    interface IPersistKey with
        member _.Key = key
        member _.Loader = loader

/// <summary>The store keys of the package's own records.</summary>
module internal PersistReserved =
    [<Literal>]
    let Prefix = "$ranvier/"

    let busterKey = Prefix + "buster"
    let positionKey = Prefix + "position"

/// <summary>
/// The restored values of one load, and the graph whose persisted inputs save to its store. A session serves one graph.
/// </summary>
type PersistSession
    internal
    (store: IStateStore, options: PersistOptions, declared: HashSet<string>, values: Dictionary<string, obj>, position: int option, busterStale: bool)
    =
    // A key's record to save, or None to delete it.
    let dirty = Dictionary<string, StoredRecord option>()
    let live = HashSet<string>()
    let mutable graph: Graph option = None
    let mutable drain: Signal<int> option = None
    let mutable busterPending = busterStale
    let mutable saves = PersistTask.completed ()
    let mutable outcome = PersistTask.completed ()

    let enqueue (work: unit -> Task) =
        let save = PersistTask.after saves work
        saves <- save
        outcome <- PersistTask.both outcome save

    // Clears the store unless it already holds the new buster, runs prepare, then saves the records with the new
    // buster. The bust stays pending until all three succeed.
    let saveBusted (prepare: unit -> Task) (records: StoredRecord list) : Task =
        let records =
            match options.Buster with
            | Some buster ->
                {
                    Key = PersistReserved.busterKey
                    Version = 1
                    Payload = buster
                    ETag = None
                }
                :: records
            | None -> records

        let clearStale () =
            PersistTask.bind (store.LoadMany [ PersistReserved.busterKey ]) (fun stored ->
                let current =
                    stored
                    |> Seq.tryFind (fun record -> record.Key = PersistReserved.busterKey)
                    |> Option.map _.Payload

                if current = options.Buster then
                    PersistTask.completed ()
                else
                    store.Clear ())

        let prepareAndSave () =
            PersistTask.andThen prepare (fun () -> store.SaveMany records)

        PersistTask.andThen (fun () -> PersistTask.andThen clearStale prepareAndSave) (fun () ->
            busterPending <- false
            PersistTask.completed ())

    // The Seq of the last entry other than a Flush recorded through the session's journal wrapper.
    let mutable journaled: int option = None
    // True from an entry other than a Flush until the next Flush entry: the graph then records a Flush entry at the end
    // of the current flush, after every effect of the flush, and the wrapper drains there.
    let mutable flushEntryDue = false
    // Whether the store may hold a position that this session's saves have not rewritten.
    let mutable positionStale = position.IsSome

    // Deletes the stored position, ahead of a save with no journal position to store.
    let deletePosition () =
        if positionStale then
            PersistTask.andThen (fun () -> store.DeleteMany [ PersistReserved.positionKey ]) (fun () ->
                positionStale <- false
                PersistTask.completed ())
        else
            PersistTask.completed ()

    let drainDirty () =
        if dirty.Count > 0 then
            let records = dirty.Values |> Seq.choose id |> List.ofSeq

            // The position follows every data record, so a save that stops part way leaves the position behind them.
            let records, prepare =
                match journaled with
                | Some seq ->
                    records
                    @ [
                        {
                            Key = PersistReserved.positionKey
                            Version = 1
                            Payload = string seq
                            ETag = None
                        }
                    ],
                    PersistTask.completed
                | None -> records, deletePosition

            let deleted =
                dirty
                |> Seq.filter (fun entry -> entry.Value.IsNone)
                |> Seq.map _.Key
                |> List.ofSeq

            dirty.Clear ()

            let save () =
                if busterPending then
                    saveBusted prepare records
                elif records.IsEmpty then
                    prepare ()
                else
                    PersistTask.andThen prepare (fun () -> store.SaveMany records)

            enqueue (fun () ->
                if deleted.IsEmpty then
                    save ()
                else
                    PersistTask.andThen save (fun () -> store.DeleteMany deleted))

    /// <summary>
    /// The journal position stored under <c>$ranvier/position</c> at load, or <c>None</c> when the store holds none.
    /// </summary>
    member _.Position = position

    /// <summary>
    /// A journal that records every entry in <c>inner</c>. Each save of the session then also stores, under
    /// <c>$ranvier/position</c> in the same <c>SaveMany</c>, the <c>Seq</c> of the last entry other than a
    /// <c>Flush</c> recorded before the save: the saved records include every input up to it. The position is the last
    /// record of the <c>SaveMany</c>, so a save that faults part way leaves the stored position behind the records.
    /// A save with no such entry to store deletes <c>$ranvier/position</c> before it saves its records. The graph's
    /// <c>GraphOptions</c> take the wrapper as their journal. A flush that records an entry then saves once, after
    /// its last effect, including the persisted inputs written by its effects.
    /// </summary>
    member _.Journal(inner: IInputJournal) : IInputJournal =
        { new IInputJournal with
            member _.Stream = inner.Stream

            member _.Record entry =
                inner.Record entry

                if entry.Kind = JournalEntryKind.Flush then
                    flushEntryDue <- false
                    drainDirty ()
                else
                    journaled <- Some entry.Seq
                    flushEntryDue <- true
        }

    /// <summary>
    /// A task that completes once every save started before the call has completed. It faults with the exception of
    /// the first save to fault since the previous call; the saves after a faulted save still run.
    /// </summary>
    member _.FlushAsync() : Task =
        let reported = outcome
        outcome <- PersistTask.after saves PersistTask.completed
        reported

    member internal _.Store = store
    member internal _.Options = options

    /// <summary>
    /// The last value written to a declared key in the session, else its restored value, or <c>None</c> when it has
    /// neither.
    /// </summary>
    member internal _.TryRestored(key: string) =
        if not (declared.Contains key) then
            raise (
                InvalidOperationException (
                    "The key \""
                    + key
                    + "\" was not declared to the load that made this session; pass it to Persist.loadAsync."
                )
            )

        match values.TryGetValue key with
        | true, value -> Some value
        | _ -> None

    /// <summary>
    /// Binds the session to <c>current</c> on first use and makes the drain: a signal written by the first change of
    /// a flush, and a root effect on it that saves every changed record in one <c>SaveMany</c>.
    /// </summary>
    /// <exception cref="T:System.InvalidOperationException">The session already serves another graph.</exception>
    member internal _.Attach(current: Graph) =
        match graph with
        | Some bound when obj.ReferenceEquals (bound, current) -> ()
        | Some _ ->
            raise (InvalidOperationException "A persist session serves one graph, and this session already serves another; load a session per graph.")
        | None ->
            graph <- Some current

            runWithOwner current.Root (fun () ->
                let signal = createSignal 0
                drain <- Some signal

                createEffectOn (fun () -> signal.Value) (fun _ ->
                    if not flushEntryDue then
                        drainDirty ()))

    /// <summary>Claims <c>key</c> for one live persisted input until the current owner is disposed.</summary>
    /// <exception cref="T:System.InvalidOperationException">A live persisted input already holds the key.</exception>
    member internal _.Claim(key: string) =
        if not (live.Add key) then
            raise (
                InvalidOperationException (
                    "The key \""
                    + key
                    + "\" already belongs to a live persisted input."
                )
            )

        onCleanup (fun () -> live.Remove key |> ignore)

    member private _.MarkEntry(key: string, entry: StoredRecord option) =
        if dirty.Count = 0 then
            match drain with
            | Some signal -> signal.Value <- signal.Value + 1
            | None -> ()

        dirty[key] <- entry

    /// <summary>
    /// Records <c>record</c> for the drain of the current flush; a later record or deletion of its key replaces it.
    /// </summary>
    member internal this.Mark(record: StoredRecord) =
        this.MarkEntry (record.Key, Some record)

    /// <summary>Records the deletion of <c>key</c> for the drain of the current flush.</summary>
    member internal this.MarkDeleted(key: string) =
        this.MarkEntry (key, None)

    /// <summary>A persisted input created for the declared <c>key</c> afterwards starts from <c>value</c>.</summary>
    member internal _.Remember(key: string, value: obj) =
        values[key] <- value

/// <summary>Declaring, loading and restoring persisted inputs.</summary>
module Persist =
    /// <summary>The prefix of the keys the package stores its own records under.</summary>
    [<Literal>]
    let ReservedPrefix = PersistReserved.Prefix

    let internal busterKey = PersistReserved.busterKey
    let internal positionKey = PersistReserved.positionKey

    let internal checkKey (key: string) =
        if String.IsNullOrEmpty key then
            invalidArg (nameof key) "A persisted key cannot be empty."

        if key.StartsWith (ReservedPrefix, StringComparison.Ordinal) then
            invalidArg
                (nameof key)
                ("The key \""
                 + key
                 + "\" is under the reserved prefix "
                 + ReservedPrefix
                 + ".")

    /// <summary>Declares a persisted value stored under <c>key</c> with <c>codec</c>.</summary>
    /// <exception cref="T:System.ArgumentException">The key is empty or begins with <c>$ranvier/</c>.</exception>
    let key (key: string) (codec: PersistCodec<'T>) : PersistKey<'T> =
        checkKey key
        PersistKey (key, codec)

    /// <summary>
    /// Declares a persisted keyed collection stored a record per row: each row under <c>key + "/" + keyText rowKey</c>,
    /// and the row key texts in insertion order under <c>key</c>. Editing a row saves that row's record; adding or
    /// removing a row also saves the order, and removing deletes the row's record.
    /// </summary>
    /// <exception cref="T:System.ArgumentException">The key is empty or begins with <c>$ranvier/</c>.</exception>
    let rows (key: string) (codec: PersistCodec<'V>) (keyOf: 'V -> 'K) (keyText: 'K -> string) : PersistRows<'K, 'V> =
        checkKey key
        PersistRows (key, codec, keyOf, Rows keyText)

    /// <summary>
    /// Declares a persisted keyed collection stored as one record under <c>key</c> holding every row in insertion
    /// order. Any edit saves that record.
    /// </summary>
    /// <exception cref="T:System.ArgumentException">The key is empty or begins with <c>$ranvier/</c>.</exception>
    let document (key: string) (codec: PersistCodec<'V>) (keyOf: 'V -> 'K) : PersistRows<'K, 'V> =
        checkKey key
        PersistRows (key, codec, keyOf, Document)

    /// <summary>
    /// Loads the stored records of <c>keys</c>. The task faults with a <c>PersistLoadException</c> naming the key when
    /// a record is newer than its codec, lacks a migration, fails a migration or fails to decode; with
    /// <c>options.DiscardUndecodable</c> set, a record that fails to decode reads as absent instead. A stored buster unequal to <c>options.Buster</c> discards every record: each save of the session clears the store
    /// before it writes, until one such save succeeds. A save skips the clear when the store already holds
    /// <c>options.Buster</c>: sessions busted to the same buster keep each other's saves.
    /// </summary>
    /// <exception cref="T:System.ArgumentException">Two of the keys share a store key.</exception>
    let loadWith (options: PersistOptions) (store: IStateStore) (keys: IPersistKey list) : Task<PersistSession> =
        let declared = HashSet<string>()

        for key in keys do
            if not (declared.Add key.Key) then
                invalidArg
                    (nameof keys)
                    ("The key \""
                     + key.Key
                     + "\" is declared more than once.")

        let names =
            busterKey
            :: positionKey
            :: (keys |> List.map _.Key)

        let lookupIn (records: StoredRecord list) =
            let byKey = Dictionary<string, StoredRecord>()

            for record in records do
                byKey[record.Key] <- record

            fun name ->
                match byKey.TryGetValue name with
                | true, record -> Some record
                | _ -> None

        let discards failure =
            match failure with
            | DecodeFailed _ -> options.DiscardUndecodable
            | _ -> false

        PersistTask.chain (store.LoadMany names) (fun records ->
            let stored = lookupIn records
            let values = Dictionary<string, obj>()

            if
                (stored busterKey |> Option.map _.Payload)
                <> options.Buster
            then
                PersistTask.result (PersistSession (store, options, declared, values, None, true))
            else
                let present = ResizeArray<IPersistKey * StoredRecord>()
                let rowNames = ResizeArray<string>()

                for key in keys do
                    match stored key.Key with
                    | None -> ()
                    | Some record ->
                        match key.Loader.RowKeys record with
                        | Result.Ok names ->
                            present.Add (key, record)
                            rowNames.AddRange names
                        | Result.Error failure when discards failure -> ()
                        | Result.Error failure -> raise (PersistLoadException (key.Key, failure))

                let rows =
                    if rowNames.Count = 0 then
                        PersistTask.result []
                    else
                        store.LoadMany (List.ofSeq rowNames)

                PersistTask.map rows (fun rowRecords ->
                    let row = lookupIn rowRecords

                    for key, record in present do
                        match key.Loader.Restore options.DiscardUndecodable record row with
                        | Result.Ok value -> values[key.Key] <- value
                        | Result.Error (name, failure) when name = key.Key && discards failure -> ()
                        | Result.Error (name, failure) -> raise (PersistLoadException (name, failure))

                    let position =
                        stored positionKey
                        |> Option.bind (fun record ->
                            match Int32.TryParse record.Payload with
                            | true, position -> Some position
                            | _ -> None)

                    PersistSession (store, options, declared, values, position, false)))

    /// <summary>Loads the stored records of <c>keys</c> with <c>PersistOptions.defaults</c>.</summary>
    /// <exception cref="T:System.ArgumentException">Two of the keys share a store key.</exception>
    let loadAsync (store: IStateStore) (keys: IPersistKey list) : Task<PersistSession> =
        loadWith PersistOptions.defaults store keys

    /// <summary>
    /// The last value a persisted input of the session wrote to <c>key</c>, else its restored value, else
    /// <c>fallback</c>.
    /// </summary>
    /// <exception cref="T:System.InvalidOperationException">The key was not passed to the load.</exception>
    let restore (session: PersistSession) (key: PersistKey<'T>) (fallback: 'T) : 'T =
        match session.TryRestored key.Key with
        | Some value -> unbox<'T> value
        | None -> fallback

    // Marks each value of `read` whose payload differs from the last marked one, starting from `marked`, and
    // remembers it for inputs created for the key later.
    let private persistReads (session: PersistSession) (key: PersistKey<'T>) (marked: string) (read: unit -> 'T) =
        let mutable marked = marked

        let mark (value: 'T) =
            let record = PersistCodec.encode key.Codec key.Key value

            if record.Payload <> marked then
                marked <- record.Payload
                session.Mark record
                session.Remember (key.Key, box value)

        createEffectOn read mark
        // Marks a write made in the batch that disposes the owner.
        onCleanup (fun () -> mark (untrack read))

    /// <summary>
    /// A signal holding <c>Persist.restore session key fallback</c>, made in the current graph and owner, so a signal
    /// re-created for the key starts from its last written value.
    /// After each flush that changes it, its value is saved to the session's store, also when the flush disposes its
    /// owner; with the session's <c>Journal</c> wrapper as the graph's journal, the changes of one flush save in one
    /// <c>SaveMany</c>.
    /// </summary>
    /// <exception cref="T:System.InvalidOperationException">
    /// The key was not passed to the load, a live persisted input already holds it, or the session serves another graph.
    /// </exception>
    let signal (session: PersistSession) (key: PersistKey<'T>) (fallback: 'T) : Signal<'T> =
        let initial = restore session key fallback
        session.Attach Graph.Current
        session.Claim key.Key
        let signal = createSignal initial
        persistReads session key (key.Codec.Codec.Encode initial) (fun () -> signal.Value)
        signal

    /// <summary>
    /// Saves the value of <c>read</c> under <c>key</c> after each flush that changes it, also when the flush disposes
    /// the current owner, until the owner is disposed. <c>read</c> is a tracked read, such as an <c>Mvu</c>'s
    /// <c>Model</c>. Its first value is saved only when it differs from the key's restored value; with no restored
    /// value, its value at the call is not saved.
    /// </summary>
    /// <example>
    /// <code lang="fsharp">
    /// let app = Mvu.create (Persist.restore session modelKey init) update
    /// Persist.track session modelKey (fun () -> app.Model)
    /// </code>
    /// </example>
    /// <exception cref="T:System.InvalidOperationException">
    /// The key was not passed to the load, a live persisted input already holds it, or the session serves another graph.
    /// </exception>
    let track (session: PersistSession) (key: PersistKey<'T>) (read: unit -> 'T) : unit =
        let restored =
            match session.TryRestored key.Key with
            | Some value -> unbox<'T> value
            | None -> untrack read

        session.Attach Graph.Current
        session.Claim key.Key
        persistReads session key (key.Codec.Codec.Encode restored) read

    /// <summary>Declares a persisted async source stored under <c>key</c>, its settled values encoded by <c>codec</c>.</summary>
    /// <exception cref="T:System.ArgumentException">The key is empty or begins with <c>$ranvier/</c>.</exception>
    let sourceKey (key: string) (codec: PersistCodec<'T>) : PersistSourceKey<'T> =
        checkKey key
        PersistSourceKey (key, codec)

    /// <summary>
    /// An async source made in the current graph and owner, settled with the last value a persisted source of the
    /// session published under <c>key</c>, else its restored value, or failed with the failure; with neither it starts
    /// pending. A restored failure is a <c>PersistedFailure</c> carrying the stored message. The restored status is
    /// published inside <c>Journal.derived</c>, so it records no journal entry, and every reader of the source sees it.
    /// After each flush that changes the source's status, the status is saved to the session's store; a return to
    /// pending deletes the record.
    /// </summary>
    /// <remarks>Register the source with <c>Journal.keySource</c> under <c>key</c> to journal its later settles.</remarks>
    /// <exception cref="T:System.InvalidOperationException">
    /// The key was not passed to the load, a live persisted input already holds it, or the session serves another graph.
    /// </exception>
    let source (session: PersistSession) (key: PersistSourceKey<'T>) : AsyncSource<'T> =
        let restored: Reading<'T> =
            match session.TryRestored key.Key with
            | Some value -> unbox value
            | None -> Reading.Pending

        let graph = Graph.Current
        session.Attach graph
        session.Claim key.Key
        let source = createAsyncSource<'T>()

        Journal.derived graph (fun () ->
            match restored with
            | Reading.Ready value -> source.Settle value
            | Reading.Failed error -> source.Fail error
            | Reading.Pending -> ())

        let encode (reading: Reading<'T>) =
            match reading with
            | Reading.Ready value -> Some (PersistFrames.encode [ "ready"; key.Codec.Codec.Encode value ])
            | Reading.Failed error -> Some (PersistFrames.encode [ "failed"; error.Message ])
            | Reading.Pending -> None

        let mutable marked = encode restored

        let mark (reading: Reading<'T>) =
            let payload = encode reading

            if payload <> marked then
                marked <- payload

                match payload with
                | Some payload ->
                    session.Mark
                        {
                            Key = key.Key
                            Version = key.Codec.Codec.Version
                            Payload = payload
                            ETag = None
                        }
                | None -> session.MarkDeleted key.Key

                session.Remember (key.Key, box reading)

        createEffectOn (fun () -> source.TryValue) mark
        // Marks a status published in the batch that disposes the owner.
        onCleanup (fun () -> mark (untrack (fun () -> source.TryValue)))
        source


    /// <summary>
    /// A keyed collection holding the last rows a persisted collection of the session wrote to <c>key</c>, else its
    /// restored rows, else none, in insertion order; made in the current graph and owner. After each flush that
    /// changes it, the changed records are saved to the session's store, also when the flush disposes its owner: a
    /// row edit saves that row's record (row layout) or the one document record (document layout).
    /// </summary>
    /// <exception cref="T:System.InvalidOperationException">
    /// The key was not passed to the load, a live persisted input already holds it, or the session serves another graph.
    /// </exception>
    let collection (session: PersistSession) (key: PersistRows<'K, 'V>) : KeyedCollection<'K, 'V> =
        let initial: 'V list =
            match session.TryRestored key.Key with
            | Some value -> unbox value
            | None -> []

        session.Attach Graph.Current
        session.Claim key.Key
        let rows = createKeyedCollection key.KeyOf

        if not initial.IsEmpty then
            rows.Edit (fun rows ->
                for value in initial do
                    rows.AddOrUpdate value)

        let values () =
            rows.Rows.Keys
            |> Array.map rows.Rows.Get
            |> List.ofArray

        let encodeAll (items: 'V list) =
            items
            |> List.map key.Codec.Codec.Encode
            |> PersistFrames.encode

        let indexRecord (texts: string seq) =
            {
                Key = key.Key
                Version = PersistFrames.IndexVersion
                Payload = PersistFrames.encode texts
                ETag = None
            }

        // Marks the records the delta changes and returns whether it marked one.
        let persist: ProjectionDelta<'K> -> bool =
            match key.Layout with
            | Document ->
                let mutable marked = encodeAll initial

                fun _ ->
                    let payload = encodeAll (values ())

                    if payload <> marked then
                        marked <- payload

                        session.Mark
                            {
                                Key = key.Key
                                Version = key.Codec.Codec.Version
                                Payload = payload
                                ETag = None
                            }

                        true
                    else
                        false
            | Rows keyText ->
                let rowKey (rowKey: 'K) =
                    key.Key + "/" + keyText rowKey

                let saved = Dictionary<string, string>()

                for value in initial do
                    saved[rowKey (key.KeyOf value)] <- key.Codec.Codec.Encode value

                let mutable index =
                    (indexRecord (initial |> List.map (key.KeyOf >> keyText))).Payload

                fun delta ->
                    let mutable changed = false

                    let save (row: 'K) =
                        let record = PersistCodec.encode key.Codec (rowKey row) (rows.Rows.Get row)

                        match saved.TryGetValue record.Key with
                        | true, payload when payload = record.Payload -> ()
                        | _ ->
                            saved[record.Key] <- record.Payload
                            session.Mark record
                            changed <- true

                    let delete (name: string) =
                        if saved.Remove name then
                            session.MarkDeleted name
                            changed <- true

                    if delta.IsReset then
                        let live = HashSet<string>()

                        for row in delta.Keys do
                            live.Add (rowKey row) |> ignore
                            save row

                        for name in List.ofSeq saved.Keys do
                            if not (live.Contains name) then
                                delete name
                    else
                        for change in delta.Changes do
                            match change.Value with
                            | KeyChange.Removed -> delete (rowKey change.Key)
                            | _ -> save change.Key

                    let order = indexRecord (delta.Keys |> Seq.map keyText)

                    if order.Payload <> index then
                        index <- order.Payload
                        session.Mark order
                        changed <- true

                    changed

        let reader = rows.Rows.NewValueReader ()

        let apply (delta: ProjectionDelta<'K>) =
            if not delta.IsEmpty && persist delta then
                session.Remember (key.Key, box (values ()))

        createEffect (fun () ->
            let delta = reader.Read ()
            untrack (fun () -> apply delta))

        // Marks an edit made in the batch that disposes the owner; it runs before the collection's own cleanup.
        onCleanup (fun () -> untrack (fun () -> apply (reader.Read ())))
        rows

    /// <summary>
    /// Replays on <c>graph</c>, restored from <c>session</c>, the entries of <c>entries</c> recorded after the
    /// session's stored position, with <c>Journal.replay mode</c>; with no stored position, every entry. An input of
    /// work started at or before the position is applied as an <c>External</c> entry in both modes. The replayed
    /// entries are recorded on <c>graph</c>'s journal from its own next <c>Seq</c>, and a position saved afterwards
    /// refers to that journal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>entries</c> is the journal of the graph that saved the snapshot. The restored graph equals the uninterrupted
    /// run when each of its keyed inputs is persisted under its journal key.
    /// </para>
    /// <para>
    /// Work running at the snapshot can start again as setup work of <c>graph</c>, such as work an effect over
    /// persisted state starts, and the re-run work produces its input again. The replay raises on an input of work
    /// started at or before the position when <c>graph</c> started setup work other than an effect's run. On .NET, an
    /// input from a continuation of an effect's body that <c>graph</c> re-runs is applied twice.
    /// </para>
    /// </remarks>
    /// <exception cref="T:System.ArgumentException">
    /// The session has a stored position and <c>entries</c> holds no entry other than a <c>Flush</c> at it.
    /// </exception>
    /// <exception cref="T:Ranvier.JournalReplayException">
    /// An entry after the position is an input of setup work, which the restored graph re-runs; an entry after the
    /// position is an input of work started at or before it, and <c>graph</c> started setup work other than an effect's
    /// run; or <c>Journal.replay</c> raises on an entry. The replay then applies no entry for the first two reasons.
    /// </exception>
    let replay (mode: ReplayMode) (session: PersistSession) (graph: Graph) (entries: JournalEntry seq) : unit =
        match session.Position with
        | None -> Journal.replay mode graph entries
        | Some position ->
            let sorted =
                entries
                |> Seq.sortBy (fun entry -> entry.Seq)
                |> Array.ofSeq

            if
                not (
                    sorted
                    |> Array.exists (fun entry ->
                        entry.Seq = position
                        && entry.Kind <> JournalEntryKind.Flush)
                )
            then
                invalidArg
                    (nameof entries)
                    ("The entries hold no input at the snapshot's position "
                     + string position
                     + "; pass the journal of the graph that saved the snapshot.")

            let tail =
                sorted
                |> Array.filter (fun entry -> entry.Seq > position)

            for entry in tail do
                match entry.Origin with
                | JournalOrigin.Produced (0, _, _) when entry.Kind <> JournalEntryKind.Flush ->
                    raise (
                        JournalReplayException (
                            entry.Seq,
                            entry.Key,
                            entry.PayloadType,
                            "the entries after the snapshot's position "
                            + string position
                            + " hold an input of setup work; a restore replays the inputs of work started after the position"
                        )
                    )
                | _ -> ()

            let setupWorks =
                Journal.setupWorksOf graph
                |> Array.filter (fun work ->
                    work = "work"
                    || work.StartsWith ("work \"", StringComparison.Ordinal))

            if setupWorks.Length > 0 then
                match
                    tail
                    |> Array.tryFind (fun entry ->
                        match entry.Origin with
                        | JournalOrigin.Produced (started, _, _) ->
                            started <= position
                            && entry.Kind <> JournalEntryKind.Flush
                        | _ -> false)
                with
                | Some entry ->
                    raise (
                        JournalReplayException (
                            entry.Seq,
                            entry.Key,
                            entry.PayloadType,
                            "the entry is an input of work started at or before the snapshot's position "
                            + string position
                            + ", and the restored graph started setup work ["
                            + String.Join (", ", setupWorks)
                            + "], which can produce it again; a restore applies such an input on a graph whose setup work is effects alone"
                        )
                    )
                | None -> ()

            tail
            |> Array.map (fun entry ->
                match entry.Origin with
                | JournalOrigin.Produced (started, _, _) when started <= position ->
                    { entry with
                        Origin = JournalOrigin.External
                    }
                | _ -> entry)
            |> Journal.replay mode graph
