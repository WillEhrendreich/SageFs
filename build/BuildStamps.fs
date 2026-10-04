// A stamp lets a pipeline stage skip work whose inputs have not changed since it last ran: the stage writes a key
// (a SHA-256 over everything the work reads) next to what it produced, and the next run compares. A stage runs
// again on ANY doubt: no stamp, a different key, an output that is not there. A skipped stage therefore proves
// exactly what a run would have, and costs a hash.
//
// Pure, so the pipeline script and the tests read the same rules. Loaded by ci-pipeline.fsx (`#load`) and compiled
// into SageFs.Tests.
namespace SageFs.Build

open System
open System.IO
open System.Security.Cryptography
open System.Text

module BuildStamps =
  /// What a stage last recorded.
  type Stamp =
    | NoStamp
    | Stamp of key: string

  [<RequireQualifiedAccess>]
  type Outputs =
    | Present
    /// The first output that was not there.
    | Missing of what: string

  [<RequireQualifiedAccess>]
  type Verdict =
    | UpToDate
    | Rebuild of because: string

  /// SHA-256 over `name=value` lines, in order. A value holding a newline cannot become another input: the
  /// canonical text escapes it.
  let keyOf (parts: (string * string) list) : string =
    parts
    |> List.map (fun (name, value) -> sprintf "%s=%s" name (value.Replace("\\", "\\\\").Replace("\n", "\\n")))
    |> String.concat "\n"
    |> fun text -> Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes text)).ToLowerInvariant()

  /// Skip only on an exact match with every output still there.
  let decide (current: string) (stamp: Stamp) (outputs: Outputs) : Verdict =
    match stamp, outputs with
    | NoStamp, _ -> Verdict.Rebuild "no stamp yet"
    | Stamp _, Outputs.Missing what -> Verdict.Rebuild(sprintf "%s is not there" what)
    | Stamp recorded, Outputs.Present ->
      match recorded = current with
      | true -> Verdict.UpToDate
      | false -> Verdict.Rebuild "an input changed"

  /// The stamp a stage recorded, or `NoStamp` when there is none, it is empty, or it cannot be read.
  let read (path: string) : Stamp =
    try
      match File.Exists path with
      | false -> NoStamp
      | true ->
        match File.ReadAllText(path).Trim() with
        | "" -> NoStamp
        | key -> Stamp key
    with _ -> NoStamp

  /// Whole or absent, never half a stamp: written beside the file and renamed over it.
  let write (path: string) (key: string) : unit =
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    let temp = path + ".tmp"
    File.WriteAllText(temp, key + "\n")
    File.Move(temp, path, true)

  /// The key file `npmCiScript` keeps inside node_modules, so deleting node_modules also deletes the stamp.
  let npmStampFile = "node_modules/.sagefs-lock-key"

  /// `sh -c` text that runs `npm ci <args>` unless node_modules is there and was installed from this exact
  /// package-lock.json by this exact node. Run in the directory that holds the lockfile.
  let npmCiScript (args: string list) : string =
    String.concat "\n"
      [ "set -e"
        "key=\"$(sha256sum package-lock.json | cut -d' ' -f1)-$(node --version)\""
        sprintf "if [ -d node_modules ] && [ -f %s ] && [ \"$(cat %s)\" = \"$key\" ]; then" npmStampFile npmStampFile
        "  echo \"npm ci skipped: package-lock.json and node are unchanged ($key)\""
        "else"
        sprintf "  npm ci %s" (String.concat " " args)
        sprintf "  echo \"$key\" > %s" npmStampFile
        "fi" ]
