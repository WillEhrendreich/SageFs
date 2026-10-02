/// `vsc shot <name>`: the full window as a PNG, optional crops of one workbench part
/// each, and a text sidecar carrying the same moment as text, so a reviewer (or a model
/// that can read images) can cross-check what it sees against what the window says.
/// Files land in OUT/shots as NNN-<name>.png, NNN-<name>.<part>.png and NNN-<name>.txt.
module LemDrive.Shot

open System
open System.IO
open System.Text.RegularExpressions
open System.Threading.Tasks
open Microsoft.Playwright
open LemDrive.VscCommand
open LemDrive.Snapshot

/// The environment variable naming the shots directory. When it is not set the directory
/// is `shots` beside the screens directory (OUT/screens, so OUT/shots).
[<Literal>]
let ShotsDirVar = "LEM_SHOTS_DIR"

/// How many digits a shot number is padded to.
[<Literal>]
let ShotNumberWidth = 3

/// The first shot's number.
[<Literal>]
let FirstShotNumber = 1

/// The most shots one directory takes, a guard against a loop.
[<Literal>]
let MostShots = 100000

/// The longest a shot name is kept.
[<Literal>]
let LongestShotName = 60

/// Where shots go: LEM_SHOTS_DIR, or `shots` beside the screens directory.
let shotsDir (screensDir: string) : string =
  match Environment.GetEnvironmentVariable ShotsDirVar with
  | null
  | "" -> Path.Combine(Path.GetDirectoryName(screensDir.TrimEnd('/')) |> Option.ofObj |> Option.defaultValue ".", "shots")
  | dir -> dir

/// A name that is safe in a file name: letters, digits, dot, dash and underscore.
let sanitize (name: string) : string =
  let cleaned = Regex.Replace(name.Trim().ToLowerInvariant(), "[^a-z0-9._-]+", "-").Trim('-')
  match cleaned with
  | "" -> DefaultShotName
  | c when c.Length > LongestShotName -> c.Substring(0, LongestShotName)
  | c -> c

/// The files one shot writes.
type Reservation =
  { Number: int
    Stem: string
    Dir: string }

module Reservation =
  let private numbered (n: int) : string = (string n).PadLeft(ShotNumberWidth, '0')

  let png (r: Reservation) : string = Path.Combine(r.Dir, r.Stem + ".png")
  let text (r: Reservation) : string = Path.Combine(r.Dir, r.Stem + ".txt")
  let region (r: Reservation) (part: Region) : string = Path.Combine(r.Dir, sprintf "%s.%s.png" r.Stem (Region.name part))

  /// The next number: one past the highest NNN- prefix already in the directory.
  let private nextNumber (dir: string) : int =
    match Directory.Exists dir with
    | false -> FirstShotNumber
    | true ->
      Directory.GetFiles dir
      |> Array.choose (fun f ->
        let m = Regex.Match(Path.GetFileName f, @"^(\d+)-")
        match m.Success with
        | true -> Some(int m.Groups[1].Value)
        | false -> None)
      |> Array.fold max (FirstShotNumber - 1)
      |> (+) 1

  /// Reserves NNN-name.txt exclusively, so two calls at once never share a number.
  let reserve (dir: string) (name: string) : Result<Reservation, string> =
    try
      Directory.CreateDirectory dir |> ignore
      let rec attempt (n: int) (tries: int) =
        match tries > MostShots with
        | true -> Result.Error(sprintf "no free shot number under %s" dir)
        | false ->
          let stem = sprintf "%s-%s" (numbered n) (sanitize name)
          let path = Path.Combine(dir, stem + ".txt")
          try
            use _fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write)
            Ok { Number = n; Stem = stem; Dir = dir }
          with :? IOException -> attempt (n + 1) (tries + 1)
      attempt (nextNumber dir) 0
    with ex -> Result.Error(sprintf "cannot reserve a shot in %s: %s" dir ex.Message)

// --- the activation time -----------------------------------------------------------

[<Literal>]
let private ExtensionId = "willehrendreich.sagefs"

let private logTime = Regex(@"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) ", RegexOptions.Compiled)

let private parseLogTime (line: string) : DateTime option =
  let m = logTime.Match line
  match m.Success, DateTime.TryParse m.Groups[1].Value with
  | true, (true, t) -> Some t
  | _ -> None

/// From an extension host log: when the host started, when the SageFs extension began to
/// activate, and the gap. The extension's own activation work is not in this log, so the
/// number says how long after the host came up it was asked to activate.
let activationFromLog (log: string) : string =
  let lines = log.Split('\n')
  let started = lines |> Array.tryFind (fun l -> l.Contains "Extension host with pid") |> Option.bind parseLogTime
  let activated = lines |> Array.tryFind (fun l -> l.Contains("_doActivateExtension " + ExtensionId)) |> Option.bind parseLogTime
  match started, activated with
  | Some s, Some a ->
    sprintf "the extension host started at %s and asked the SageFs extension to activate %d ms later" (s.ToString "HH:mm:ss.fff") (int (a - s).TotalMilliseconds)
  | None, Some a -> sprintf "the SageFs extension was asked to activate at %s (the host start is not in the log)" (a.ToString "HH:mm:ss.fff")
  | _, None -> "the extension host log has no activation of the SageFs extension yet"

/// Reads the newest extension host log under the run's VS Code profile.
let activation (runDir: string) : string =
  let logs = Path.Combine(runDir, ".lem", "vsc", "logs")
  try
    match Directory.Exists logs with
    | false -> "unknown: no VS Code logs under the run directory"
    | true ->
      Directory.GetDirectories logs
      |> Array.sortDescending
      |> Array.tryPick (fun d ->
        let f = Path.Combine(d, "window1", "exthost", "exthost.log")
        match File.Exists f with
        | true -> Some f
        | false -> None)
      |> Option.map (fun f ->
        use stream = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
        use reader = new StreamReader(stream)
        activationFromLog (reader.ReadToEnd()))
      |> Option.defaultValue "unknown: no extension host log yet"
  with ex -> sprintf "unknown: %s" ex.Message

// --- capturing ---------------------------------------------------------------------

/// A read-only reader for the window's inner size.
[<Literal>]
let ReadInnerSize = "() => window.innerWidth + 'x' + window.innerHeight"

let private partSelector (r: Region) : string =
  match r with
  | ActivityBar -> ".part.activitybar"
  | SideBar -> ".part.sidebar"
  | Editor -> ".part.editor"
  | Panel -> ".part.panel"
  | StatusBar -> ".part.statusbar"

let private windowSize (page: IPage) : Task<int * int> =
  task {
    let! text = page.EvaluateAsync<string>(ReadInnerSize)
    match text.Split('x') with
    | [| w; h |] -> return int w, int h
    | _ -> return 0, 0
  }

/// Captures one shot: the full window, each requested part that is showing, and the sidecar.
/// Returns what was written, one path per line, and the parts that were not showing.
let capture (c: Cdp.Connection) (runDir: string) (r: Reservation) (regions: Region list) : Task<string> =
  task {
    let! facts = Cdp.facts c
    let! width, height = windowSize c.Page
    let! _ = c.Page.ScreenshotAsync(PageScreenshotOptions(Path = Reservation.png r))
    let written = ResizeArray<string>([ Reservation.png r ])
    let missing = ResizeArray<string>()
    for part in regions do
      let loc = c.Page.Locator(partSelector part).First
      let! showing = loc.IsVisibleAsync()
      match showing with
      | true ->
        let! _ = loc.ScreenshotAsync(LocatorScreenshotOptions(Path = Reservation.region r part))
        written.Add(Reservation.region r part)
      | false -> missing.Add(Region.name part)
    let sidecar =
      renderSidecar
        { Name = r.Stem
          Regions = regions |> List.map Region.name
          Width = width
          Height = height
          TakenAt = DateTime.UtcNow.ToString("o")
          Activation = activation runDir
          Facts = facts }
    let note =
      match List.ofSeq missing with
      | [] -> ""
      | parts -> sprintf "\nnot showing, so not cropped: %s\n" (String.Join(", ", parts))
    File.WriteAllText(Reservation.text r, sidecar + note)
    written.Add(Reservation.text r)
    return String.Join("\n", written)
  }
