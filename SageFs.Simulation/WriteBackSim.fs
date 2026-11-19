namespace SageFs.Simulation

open SageFs.Features.Tweak

/// Deterministic Simulation Testing for the edit door.
///
/// The two decisions under test are the REAL pure functions:
/// `WriteBackRequest.plan` (can this request even start?) and
/// `EvalWriteBack.decide` (where exactly does it land in the file?). Everything
/// else here is the world around them: which files the session owns, what their
/// source currently says, and which requests arrived in which order.
///
/// Chaos is data. A Scenario is an ordered op list, so the same ops give the
/// same trace and a failing seed replays verbatim. Ground truth (what bytes are
/// actually in each file) is an independent fold that never calls either
/// decision under test.
module WriteBackSim =

  /// The files of the session. Only these can be written.
  let ownedFile = "/repo/App.fs"
  let otherFile = "/repo/Other.fs"

  /// A declaration that lives at a known line in the owned file.
  let sourceBefore = "module App\n\nlet answer = 1\n"
  let declarationLine = 3

  [<RequireQualifiedAccess>]
  type Op =
    /// A well-formed replacement of the declaration.
    | ValidEdit
    /// The same edit again, after it was already written.
    | RepeatedEdit
    /// F# source that is an expression, not a declaration.
    | BareExpression
    /// A request naming a file the session does not own.
    | ForeignFile
    /// A request whose line is not in the declaration.
    | WrongLine
    /// A request with no file at all.
    | NoFile
    /// A request with an empty line.
    | NoLine

  type Scenario = { Seed: int; Ops: Op list }

  [<RequireQualifiedAccess>]
  type Effect =
    /// The pure front door refused; nothing ran and nothing was touched.
    | Refused of WriteBackRequest.Refusal
    /// The eval succeeded but the splice decision refused; the file is unchanged.
    | NotPersisted of reason: string
    /// The declaration was written, with the exact replacement text.
    | Written of file: string * line: int * replacement: string

  type Step = { Op: Op; Effects: Effect list; Files: Map<string, string> }

  type Trace = { Scenario: Scenario; Steps: Step list }

  let private codeFor = function
    | Op.ValidEdit | Op.RepeatedEdit | Op.ForeignFile | Op.WrongLine -> "let answer = 2;;"
    | Op.BareExpression -> "42;;"
    | Op.NoFile | Op.NoLine -> "let answer = 2;;"

  let private fileFor = function
    | Op.ForeignFile -> Some otherFile
    | Op.NoFile -> None
    | _ -> Some ownedFile

  let private lineFor = function
    | Op.WrongLine -> Some 99
    | Op.NoLine -> None
    | _ -> Some declarationLine

  /// The fold. `files` is the independent ground truth: only an `Ok splice`
  /// from the real decision can change it, and it changes it through the real
  /// `applySplice`. No test helper reimplements the splice.
  let run (scenario: Scenario) : Trace =
    let initial = Map.ofList [ ownedFile, sourceBefore ]
    let _, steps =
      scenario.Ops
      |> List.fold (fun (files, acc) op ->
        let request =
          { WriteBackRequest.Request.Code = codeFor op
            WriteBackRequest.Request.FilePath = fileFor op |> Option.defaultValue ""
            WriteBackRequest.Request.BlockStartLine = lineFor op |> Option.defaultValue 0 }
        let effects, files' =
          match WriteBackRequest.plan request with
          | Error refusal -> [ Effect.Refused refusal ], files
          | Ok plan ->
            let source = files |> Map.tryFind plan.FilePath |> Option.defaultValue ""
            match EvalWriteBack.decide plan.Code (Some plan.FilePath) (Some plan.BlockStartLine) source with
            | Error (EvalWriteBack.WriteBack.NotPersisted reason) -> [ Effect.NotPersisted reason ], files
            | Error (EvalWriteBack.WriteBack.Written _) -> [], files
            | Ok splice ->
              match EvalWriteBack.applySplice source splice with
              | Ok text ->
                [ Effect.Written(plan.FilePath, plan.BlockStartLine, splice.Replacement) ],
                files |> Map.add plan.FilePath text
              | Error (EvalWriteBack.WriteBack.NotPersisted reason) -> [ Effect.NotPersisted reason ], files
              | Error (EvalWriteBack.WriteBack.Written _) -> [], files
        files', acc @ [ { Op = op; Effects = effects; Files = files' } ]) (initial, [])
    { Scenario = scenario; Steps = steps }
