open System.IO
let reg (p: string) (entry: string) (after: string) =
  let lines = File.ReadAllLines(p) |> Array.toList
  if lines |> List.exists (fun (l: string) -> l.Contains(entry)) then
    printfn "already: %s" entry
  else
    let i = lines |> List.findIndex (fun (l: string) -> l.Contains(after))
    let indent = lines.[i].Substring(0, max 0 (lines.[i].IndexOf('<')))
    let e = sprintf "%s<Compile Include=\"%s\" />" indent entry
    File.WriteAllLines(p, (lines |> List.truncate (i+1)) @ (e :: (lines |> List.skip (i+1))))
    printfn "registered %s after %s" entry after
let main = "/home/will/Work/SageFs/SageFs/SageFs.fsproj"
let tests = "/home/will/Work/SageFs/SageFs.Tests/SageFs.Tests.fsproj"
let ml = File.ReadAllLines(main) |> Array.toList
if not (ml |> List.exists (fun (l: string) -> l.Contains("WorkerProxyWait.fs"))) then
  let i = ml |> List.findIndex (fun (l: string) -> l.Contains("<Compile"))
  let indent = ml.[i].Substring(0, max 0 (ml.[i].IndexOf('<')))
  let e = sprintf "%s<Compile Include=\"WorkerProxyWait.fs\" />" indent
  File.WriteAllLines(main, (ml |> List.truncate i) @ (e :: (ml |> List.skip i)))
  printfn "registered WorkerProxyWait.fs first in SageFs.fsproj"
reg tests "WorkerProxyWaitTests.fs" "TestSummaryCompletenessTests.fs"
