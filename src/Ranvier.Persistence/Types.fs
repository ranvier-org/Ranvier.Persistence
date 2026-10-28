namespace Ranvier.Persistence

open System
open System.Threading.Tasks
open Ranvier

/// <summary>A stored value: its key, the codec version of its payload, and the encoded payload.</summary>
/// <remarks><c>ETag</c> is reserved for stores with optimistic concurrency; the v1 stores ignore it.</remarks>
type StoredRecord =
    {
        Key: string
        Version: int
        Payload: string
        ETag: string option
    }

/// <summary>A keyed record store. The last <c>SaveMany</c> of a key wins.</summary>
type IStateStore =
    /// <summary>The stored records of the keys present in the store, in any order.</summary>
    abstract LoadMany: keys: string list -> Task<StoredRecord list>
    /// <summary>Stores each record under its key, replacing the record stored there.</summary>
    abstract SaveMany: records: StoredRecord list -> Task
    /// <summary>Removes the records of the keys; an absent key is skipped.</summary>
    abstract DeleteMany: keys: string list -> Task
    /// <summary>Removes every record of the store.</summary>
    abstract Clear: unit -> Task

/// <summary>Rewrites a payload stored at version <c>From</c> into its version <c>From + 1</c> form.</summary>
type PersistMigration =
    {
        From: int
        Migrate: string -> Result<string, string>
    }

/// <summary>
/// A journal codec and the migrations that bring an older payload up to the codec's <c>Version</c>.
/// </summary>
type PersistCodec<'T> =
    {
        Codec: JournalCodec<'T>
        Migrations: PersistMigration list
    }

/// <summary>Why a stored record cannot be restored.</summary>
type PersistFailure =
    /// <summary>The record's version is greater than the codec's.</summary>
    | NewerVersion of stored: int * supported: int
    /// <summary>The codec has no migration from this version.</summary>
    | MissingMigration of from: int
    /// <summary>The migration from this version returned an error.</summary>
    | MigrationFailed of from: int * message: string
    /// <summary>The codec's <c>Decode</c> returned an error.</summary>
    | DecodeFailed of message: string

/// <summary>The failure of a load: the key of the record and why it cannot be restored.</summary>
type PersistLoadException(key: string, failure: PersistFailure) =
    inherit
        Exception(
            "Persisted key \""
            + key
            + "\" cannot be restored: "
            + (match failure with
               | NewerVersion (stored, supported) ->
                   "stored version "
                   + string stored
                   + " is newer than the codec's version "
                   + string supported
               | MissingMigration from -> "no migration from version " + string from
               | MigrationFailed (from, message) ->
                   "the migration from version "
                   + string from
                   + " failed: "
                   + message
               | DecodeFailed message -> "decoding failed: " + message)
        )

    /// <summary>The key of the record.</summary>
    member _.Key = key

    /// <summary>Why the record cannot be restored.</summary>
    member _.Failure = failure

/// <summary>
/// The failure of a restored async source: the stored failure's <c>Message</c>, as a replay's
/// <c>JournalReplayedFailure</c> carries a recorded one.
/// </summary>
type PersistedFailure(message: string) =
    inherit Exception(message)

/// <summary>The options of a load.</summary>
type PersistOptions =
    {
        /// <summary>The store-wide buster. A stored buster unequal to it discards every record of the load.</summary>
        Buster: string option
        /// <summary>
        /// When true, a record that fails to decode (<c>DecodeFailed</c>) is discarded and its key uses the fallback;
        /// when false, it fails the load with a <c>PersistLoadException</c>. A newer version, a missing migration and
        /// a failed migration fail the load either way.
        /// </summary>
        DiscardUndecodable: bool
    }

/// <summary>The load options.</summary>
module PersistOptions =
    /// <summary>No buster, and a record that cannot be restored fails the load.</summary>
    let defaults =
        {
            Buster = None
            DiscardUndecodable = false
        }
