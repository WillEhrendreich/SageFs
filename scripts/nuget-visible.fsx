// scripts/nuget-visible.fsx <package id> <version> [--timeout <seconds>] [--since <iso time>] [--log <file>]
//
// Polls nuget.org's flat container until <version> of <package id> can be downloaded, then says how long that took
// and appends one line to --log. This is the wait after a green publish that nobody had ever measured: the push
// succeeds, and the flat container (what `dotnet tool update` reads) can still answer 404 for a while. The wait is
// NuGet's, so this does not try to shorten it; it records it, so the next ship knows the number.
//
// `--since` is when the push finished, so the recorded lag is push to visible and not poller start to visible.
//
// Exit codes: 0 visible; 1 still not visible at the timeout; 64 bad arguments.
open System
open System.IO
open System.Net.Http

let pollEvery = TimeSpan.FromSeconds 5.
let requestTimeout = TimeSpan.FromSeconds 10.
let defaultTimeoutSeconds = 900.

let args = fsi.CommandLineArgs |> Array.skip 1 |> Array.filter (fun a -> a <> "--") |> List.ofArray

let rec option (name: string) (xs: string list) : string option =
  match xs with
  | n :: v :: _ when n = name -> Some v
  | _ :: rest -> option name rest
  | [] -> None

match args with
| id :: version :: _ when not (id.StartsWith "--") && not (version.StartsWith "--") ->
  let timeout =
    match option "--timeout" args |> Option.map Double.TryParse with
    | Some (true, s) -> TimeSpan.FromSeconds s
    | _ -> TimeSpan.FromSeconds defaultTimeoutSeconds
  let since =
    match option "--since" args |> Option.map DateTimeOffset.TryParse with
    | Some (true, t) -> t
    | _ -> DateTimeOffset.UtcNow
  let url = sprintf "https://api.nuget.org/v3-flatcontainer/%s/%s/%s.nuspec" (id.ToLowerInvariant()) (version.ToLowerInvariant()) (id.ToLowerInvariant())
  use http = new HttpClient(Timeout = requestTimeout)
  let status () =
    try
      use reply = http.GetAsync(url).GetAwaiter().GetResult()
      int reply.StatusCode
    with _ -> 0
  let deadline = DateTimeOffset.UtcNow + timeout
  let rec wait (attempts: int) (lastStatus: int) =
    match status () with
    | 200 -> Some (attempts + 1, 200)
    | code when DateTimeOffset.UtcNow >= deadline ->
      printfn "last status %d" code
      None
    | _ ->
      Threading.Thread.Sleep pollEvery
      wait (attempts + 1) 0
  let outcome = wait 0 0
  let now = DateTimeOffset.UtcNow
  let lag = (now - since).TotalSeconds
  let line =
    match outcome with
    | Some (probes, _) -> sprintf "%s %s %s visible %.0fs after the push (%d probes)" (now.ToString "o") id version lag probes
    | None -> sprintf "%s %s %s NOT visible %.0fs after the push (gave up)" (now.ToString "o") id version lag
  printfn "%s" line
  match option "--log" args with
  | Some path ->
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath path)) |> ignore
    File.AppendAllText(path, line + "\n")
  | None -> ()
  exit (match outcome with Some _ -> 0 | None -> 1)
| _ ->
  eprintfn "usage: nuget-visible.fsx <package id> <version> [--timeout <seconds>] [--since <iso time>] [--log <file>]"
  exit 64
