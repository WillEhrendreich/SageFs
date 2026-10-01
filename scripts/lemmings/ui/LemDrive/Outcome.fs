/// What one driver call came to, the environment the harness hands the driver,
/// and the numbered log every call leaves behind. Shared by the VS Code and
/// Neovim commands so a transcript reads the same for both editors.
module LemDrive.Calls

open System
open System.IO

/// The result of one driver call. A refusal is not a failure: the driver would
/// not do what was asked (a guarded command, a bad chord), and says why.
type Outcome =
  | Output of text: string
  | Refused of reason: string
  | DriveFailed of reason: string
  | BadUsage of reason: string

module Outcome =
  /// The process exit code for each outcome. 0 means the call did its work.
  let exitCode (outcome: Outcome) : int =
    match outcome with
    | Output _ -> 0
    | DriveFailed _ -> 1
    | BadUsage _ -> 2
    | Refused _ -> 3

  let label (outcome: Outcome) : string =
    match outcome with
    | Output _ -> "ok"
    | DriveFailed _ -> "failed"
    | BadUsage _ -> "usage"
    | Refused _ -> "refused"

  let text (outcome: Outcome) : string =
    match outcome with
    | Output t -> t
    | DriveFailed r -> sprintf "FAILED: %s" r
    | BadUsage r -> sprintf "USAGE: %s" r
    | Refused r -> sprintf "REFUSED: %s" r

/// The environment variables the harness sets for the driver. One closed set,
/// one exhaustive name function.
type DriverEnv =
  | CdpPort
  | ScreensDir
  | RunDir

module DriverEnv =
  let name (e: DriverEnv) : string =
    match e with
    | CdpPort -> "LEM_CDP_PORT"
    | ScreensDir -> "LEM_SCREENS_DIR"
    | RunDir -> "LEM_RUN_DIR"

  let read (e: DriverEnv) : Result<string, string> =
    match Environment.GetEnvironmentVariable(name e) with
    | null -> Result.Error(sprintf "%s is not set" (name e))
    | "" -> Result.Error(sprintf "%s is empty" (name e))
    | value -> Ok value

/// How many digits a screen number is padded to, so the files sort in call order.
[<Literal>]
let ScreenNumberWidth = 3

/// How many numbers to try before giving up on reserving one.
[<Literal>]
let ScreenReserveAttempts = 100000

/// One reserved slot in OUT/screens: NNN.txt holds the transcript of the call,
/// NNN.png is where a screenshot goes.
type Screen =
  { Number: int
    TextPath: string
    ImagePath: string }

module Screens =
  let private pathFor (dir: string) (n: int) (ext: string) : string =
    Path.Combine(dir, (string n).PadLeft(ScreenNumberWidth, '0') + "." + ext)

  /// Reserves the next free number by creating NNN.txt exclusively, so two
  /// driver calls that overlap can never share a number.
  let reserve (dir: string) : Result<Screen, string> =
    try
      Directory.CreateDirectory dir |> ignore
      let rec attempt (n: int) =
        match n > ScreenReserveAttempts with
        | true -> Result.Error(sprintf "no free screen number under %s" dir)
        | false ->
          let textPath = pathFor dir n "txt"
          try
            use _fs = new FileStream(textPath, FileMode.CreateNew, FileAccess.Write)
            Ok { Number = n; TextPath = textPath; ImagePath = pathFor dir n "png" }
          with :? IOException -> attempt (n + 1)
      attempt 1
    with ex -> Result.Error(sprintf "cannot reserve a screen in %s: %s" dir ex.Message)

  /// Writes the transcript: what was asked, what came back, how it ended.
  let write (screen: Screen) (argv: string list) (outcome: Outcome) : unit =
    let body =
      String.concat
        "\n"
        [ sprintf "# %s" (String.Join(' ', argv))
          sprintf "# %s  %s" (DateTime.UtcNow.ToString("o")) (Outcome.label outcome)
          Outcome.text outcome
          "" ]
    try File.WriteAllText(screen.TextPath, body) with _ -> ()
