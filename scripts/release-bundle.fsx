// Reads the release bundle's manifest, checks every file in it against its
// recorded SHA-256, and writes version/source_sha to GITHUB_OUTPUT. Run from
// the repo root, with the bundle already downloaded:
//
//   dotnet fsi scripts/release-bundle.fsx release
//
// Exit 0 when the bundle is whole, 1 when anything is missing, altered or
// unreadable. Nothing is published on a 1.

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json

let bundleDir =
  match fsi.CommandLineArgs |> Array.tryItem 1 with
  | Some dir -> dir
  | None -> "release"

let manifestPath = Path.Combine(bundleDir, "release-manifest.json")

let die (message: string) =
  eprintfn "::error::%s" message
  exit 1

if not (File.Exists manifestPath) then die (sprintf "%s not found." manifestPath)

let manifest = JsonDocument.Parse(File.ReadAllText manifestPath).RootElement

let required (name: string) =
  match manifest.TryGetProperty name with
  | true, v when v.ValueKind = JsonValueKind.String && v.GetString() <> "" -> v.GetString()
  | _ ->
    die (sprintf "Release manifest is missing %s." name)
    ""

let version = required "version"
let sourceSha = required "sourceSha"

let files =
  match manifest.TryGetProperty "files" with
  | true, files when files.ValueKind = JsonValueKind.Array -> files.EnumerateArray() |> List.ofSeq
  | _ ->
    die "Release manifest has no files array."
    []

let sha256Of (path: string) =
  use stream = File.OpenRead path
  use sha = SHA256.Create()
  sha.ComputeHash stream
  |> Array.map (fun b -> b.ToString "x2")
  |> String.concat ""

let problems =
  files
  |> List.choose (fun file ->
    let name = file.GetProperty("name").GetString()
    let expected = file.GetProperty("sha256").GetString().ToLowerInvariant()
    let path = Path.Combine(bundleDir, name)
    match File.Exists path with
    | false -> Some(sprintf "Missing artifact: %s" name)
    | true ->
      let actual = sha256Of path
      match actual = expected with
      | true -> None
      | false -> Some(sprintf "Checksum mismatch for %s: the bundle says %s, the file is %s" name expected actual))

match problems with
| [] ->
  printfn "Release bundle %s is whole: %d files, every checksum matches." version (List.length files)
  match Environment.GetEnvironmentVariable "GITHUB_OUTPUT" with
  | null | "" -> printfn "version=%s\nsource_sha=%s" version sourceSha
  | output -> File.AppendAllText(output, sprintf "version=%s\nsource_sha=%s\n" version sourceSha)
  exit 0
| _ ->
  for problem in problems do
    eprintfn "::error::%s" problem
  exit 1
