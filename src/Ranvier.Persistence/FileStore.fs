namespace Ranvier.Persistence

#if !FABLE_COMPILER
open System
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks

/// <summary>The file names and file format of <c>FileStateStore</c>.</summary>
module internal FileRecord =
    [<Literal>]
    let Header = "ranvier-record"

    [<Literal>]
    let Extension = ".record"

    [<Literal>]
    let private MaxEscaped = 160

    let private utf8 = UTF8Encoding false

    let private hex (bytes: byte array) =
        let text = StringBuilder (bytes.Length * 2)

        for b in bytes do
            text.Append (b.ToString ("x2", CultureInfo.InvariantCulture))
            |> ignore

        text.ToString ()

    /// <summary>
    /// The file name of <c>key</c>: distinct per key on case-insensitive file systems, and never a device name. Lower-case
    /// ASCII letters, digits and <c>-</c> are kept; every other UTF-8 byte is written <c>_hh</c>. A key whose escaped
    /// form is long is named by its SHA-256 hash.
    /// </summary>
    let fileName (key: string) =
        let escaped = StringBuilder ()

        for b in utf8.GetBytes key do
            let c = char b

            if
                (c >= 'a' && c <= 'z')
                || (c >= '0' && c <= '9')
                || c = '-'
            then
                escaped.Append c |> ignore
            else
                escaped.Append('_').Append(b.ToString ("x2", CultureInfo.InvariantCulture))
                |> ignore

        if escaped.Length <= MaxEscaped then
            "k-" + escaped.ToString () + Extension
        else
            use sha = SHA256.Create ()

            "h-"
            + hex (sha.ComputeHash (utf8.GetBytes key))
            + Extension

    /// <summary>The file text of <c>record</c>: the header, the version, the key's length, then the key and the payload.</summary>
    let format (record: StoredRecord) =
        String.Concat (
            [|
                Header
                "\n"
                record.Version.ToString CultureInfo.InvariantCulture
                "\n"
                record.Key.Length.ToString CultureInfo.InvariantCulture
                "\n"
                record.Key
                record.Payload
            |]
        )

    /// <summary>The record of the file text, or <c>None</c> when the text is not a record of <c>key</c>.</summary>
    let parse (key: string) (text: string) : StoredRecord option =
        let lines = text.Split ([| '\n' |], 4)

        if lines.Length = 4 && lines[0] = Header then
            match
                Int32.TryParse (lines[1], NumberStyles.Integer, CultureInfo.InvariantCulture),
                Int32.TryParse (lines[2], NumberStyles.None, CultureInfo.InvariantCulture)
            with
            | (true, version), (true, length) when
                length = key.Length
                && lines[3].StartsWith(key, StringComparison.Ordinal)
                ->
                Some
                    {
                        Key = key
                        Version = version
                        Payload = lines[3].Substring length
                        ETag = None
                    }
            | _ -> None
        else
            None

    let encoding = utf8

    /// <summary>Renames <c>temporary</c> over <c>file</c>, replacing it; deletes <c>temporary</c> when the rename fails.</summary>
    let replace (temporary: string) (file: string) =
        try
#if NETSTANDARD2_1
            if File.Exists file then
                File.Replace (temporary, file, null)
            else
                File.Move (temporary, file)
#else
            File.Move (temporary, file, true)
#endif
        with _ ->
            File.Delete temporary
            reraise ()

/// <summary>
/// An <c>IStateStore</c> keeping one file per key in <c>directory</c>. A save writes each record to a temporary file,
/// flushes it to disk, then renames it over the key's file, so a crash leaves each key's earlier record or its new
/// one. A save creates the directory when it is missing. The v1 store ignores <c>ETag</c>; the last writer wins.
/// </summary>
type FileStateStore(directory: string) =
    let path (key: string) =
        Path.Combine (directory, FileRecord.fileName key)

    let load (key: string) : Task<StoredRecord option> =
        task {
            let file = path key

            if not (File.Exists file) then
                return None
            else
                let! text = File.ReadAllTextAsync (file, FileRecord.encoding)

                match FileRecord.parse key text with
                | Some record -> return Some record
                | None ->
                    return
                        raise (
                            InvalidDataException (
                                "The file \""
                                + file
                                + "\" does not hold a record of the key \""
                                + key
                                + "\"."
                            )
                        )
        }

    let save (record: StoredRecord) : Task =
        task {
            let file = path record.Key
            let temporary = file + "." + Guid.NewGuid().ToString "N" + ".tmp"
            let bytes = FileRecord.encoding.GetBytes (FileRecord.format record)

            do!
                task {
                    use stream =
                        new FileStream (temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true)

                    do! stream.WriteAsync (bytes, 0, bytes.Length)
                    stream.Flush true
                }

            FileRecord.replace temporary file
        }

    /// <summary>The directory the store keeps its files in.</summary>
    member _.Directory = directory

    interface IStateStore with
        member _.LoadMany(keys: string list) =
            task {
                let records = ResizeArray ()

                for key in List.distinct keys do
                    let! record = load key

                    match record with
                    | Some record -> records.Add record
                    | None -> ()

                return List.ofSeq records
            }

        member _.SaveMany(records: StoredRecord list) =
            task {
                Directory.CreateDirectory directory |> ignore

                for record in records do
                    do! save record
            }

        member _.DeleteMany(keys: string list) =
            task {
                for key in keys do
                    let file = path key

                    if File.Exists file then
                        File.Delete file
            }

        member _.Clear() =
            task {
                if Directory.Exists directory then
                    for file in Directory.GetFiles (directory, "*" + FileRecord.Extension) do
                        File.Delete file
            }
#endif
