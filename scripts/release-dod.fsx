// The release Definition of Done gate: no row in quality/definition-of-done.json
// may still be deferred when a release goes out. Run from the repo root.
//
//   dotnet fsi scripts/release-dod.fsx
//
// Exit 0 when nothing is deferred, 1 when something is, naming every row and
// the issue that tracks it.

open System
open System.IO
open System.Text.Json

let matrixPath = Path.Combine("quality", "definition-of-done.json")

match File.Exists matrixPath with
| false ->
  eprintfn "::error::%s not found, so the release gate can't be checked." matrixPath
  exit 1
| true ->

let doc = JsonDocument.Parse(File.ReadAllText matrixPath)

let text (row: JsonElement) (name: string) =
  match row.TryGetProperty name with
  | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
  | true, v -> v.ToString()
  | false, _ -> ""

let rows =
  match doc.RootElement.TryGetProperty "rows" with
  | true, rows when rows.ValueKind = JsonValueKind.Array -> rows.EnumerateArray() |> List.ofSeq
  | _ ->
    eprintfn "::error::%s has no rows array." matrixPath
    exit 1

let deferred = rows |> List.filter (fun row -> text row "status" = "deferred")

match deferred with
| [] ->
  printfn "Definition of Done: %d rows, none deferred." (List.length rows)
  exit 0
| _ ->
  for row in deferred do
    eprintfn
      "::error::Release blocked by %s (issue #%s, expires %s)"
      (text row "id")
      (text row "issue")
      (text row "expires")
  eprintfn "Release Definition of Done has %d deferred obligation(s)." (List.length deferred)
  exit 1
