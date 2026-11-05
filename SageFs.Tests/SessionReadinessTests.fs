/// What a session's "Ready" is worth. `Readiness.decide` joins the daemon's state, the source state (the disk is ahead of the
/// build) and the REPL's freshness (the REPL is behind the app) into one answer that the header badge, the daemon health bar,
/// the statusline and the session card all read. These are the claims it keeps, over every combination of the three:
///
///   * A Ready session whose build is not known to be current never reads as a plain "Ready".
///   * Red is for failure only: a build that is behind the files is a caution, never a failure.
///   * A state other than Ready keeps the daemon's own word, which clients wait on.
module SageFs.Tests.SessionReadinessTests

open System
open Expecto
open Expecto.Flip
open SageFs

let private t0 = DateTime(2026, 11, 5, 16, 0, 0, DateTimeKind.Utc)

let private everyState =
  [ SessionState.Uninitialized; SessionState.WarmingUp; SessionState.Ready; SessionState.Evaluating; SessionState.Faulted ]

let private staleFile = { Path = "/repo/A.fs"; Because = StaleBecause.EditedAfterBuild (t0.AddMinutes 5.0, t0) }

/// One value of every case of `SourceState`, and of every case of what an unknown can be about.
let private everySource =
  [ SourceState.InSync (t0, 3)
    SourceState.Stale [ staleFile ]
    SourceState.Rebuilding t0
    SourceState.Unknown UnknownReason.NoProjectLoaded
    SourceState.Unknown UnknownReason.NotAssessed
    SourceState.Unknown (UnknownReason.LoadTimeNotReported "no report")
    SourceState.Unknown (UnknownReason.Unreadable ("/repo/A.fs", "denied"))
    SourceState.Unknown (UnknownReason.NotInspectable ("/repo/P.fsproj", "wildcard")) ]

let private everyFreshness =
  [ ReplFreshness.InSync
    ReplFreshness.BehindApp (2, [ "Handlers.describe" ])
    ReplFreshness.Unknown FreshnessUnknown.NotRead ]

let private every : (SessionState * SourceState * ReplFreshness) list =
  [ for state in everyState do
      for source in everySource do
        for freshness in everyFreshness -> state, source, freshness ]

let private isFine (source: SourceState) (freshness: ReplFreshness) : bool =
  match source, freshness with
  | (SourceState.InSync _ | SourceState.Unknown UnknownReason.NoProjectLoaded), ReplFreshness.InSync -> true
  | _ -> false

[<Tests>]
let readinessTests =
  testList "Readiness.decide" [

    testCase "WHY — a Ready session with a current build and a level REPL is plain Ready, healthy, in the daemon's own word" <| fun _ ->
      let readiness = Readiness.decide SessionState.Ready (SourceState.InSync (t0, 3)) ReplFreshness.InSync
      readiness |> Expect.equal "current" Readiness.ReadyCurrent
      Readiness.label readiness |> Expect.equal "the word clients wait on" "Ready"
      Readiness.tone readiness |> Expect.equal "green" ReadinessTone.Healthy

    testCase "WHY — a Ready session whose files changed after its build says it runs older code, in words, and in a caution tone" <| fun _ ->
      let readiness = Readiness.decide SessionState.Ready (SourceState.Stale [ staleFile ]) ReplFreshness.InSync
      readiness |> Expect.equal "older code" Readiness.ReadyOlderCode
      Readiness.label readiness |> Expect.equal "plain words" "Ready, running older code"
      Readiness.tone readiness |> Expect.equal "stale is a warning" ReadinessTone.Caution

    testCase "WHY — a rebuild in progress is said as such, because the session keeps serving the old build until it finishes" <| fun _ ->
      Readiness.decide SessionState.Ready (SourceState.Rebuilding t0) ReplFreshness.InSync
      |> Readiness.label
      |> Expect.equal "rebuilding" "Ready, rebuilding"

    testCase "WHY — a REPL behind the app is said even when the files match the build, because it is a different fact from the disk being ahead" <| fun _ ->
      Readiness.decide SessionState.Ready (SourceState.InSync (t0, 3)) (ReplFreshness.BehindApp (1, []))
      |> Readiness.label
      |> Expect.equal "behind the app" "Ready, REPL behind the app"

    testCase "WHY — not being able to tell is said, and is not the same as being current" <| fun _ ->
      Readiness.decide SessionState.Ready (SourceState.Unknown (UnknownReason.LoadTimeNotReported "no report")) ReplFreshness.InSync
      |> Readiness.label
      |> Expect.equal "unchecked" "Ready, build not checked"

    testCase "WHY — a session with no project has no build to be behind, so it is plain Ready and not an amber warning on every bare session" <| fun _ ->
      Readiness.decide SessionState.Ready (SourceState.Unknown UnknownReason.NoProjectLoaded) ReplFreshness.InSync
      |> Expect.equal "current" Readiness.ReadyCurrent

    testCase "WHY — a state read without looking at the source is never ReadyCurrent for a Ready session" <| fun _ ->
      Readiness.ofStateAlone SessionState.Ready |> Expect.equal "unchecked, not current" Readiness.ReadyUnchecked
      Readiness.ofStateAlone SessionState.Faulted |> Expect.equal "other states keep their word" (Readiness.InState SessionState.Faulted)

    testCase "WHY — over every combination, Ready reads as plain Ready exactly when the source is current (or absent) and the REPL is level" <| fun _ ->
      every
      |> List.iter (fun (state, source, freshness) ->
        let readiness = Readiness.decide state source freshness
        let plain = Readiness.label readiness = "Ready"
        let expected = state = SessionState.Ready && isFine source freshness
        plain |> Expect.equal (sprintf "%A / %A / %A" state source freshness) expected)

    testCase "WHY — over every combination, a state other than Ready keeps the daemon's own word whatever the build says (WarmingUp, a token in code, is spelled 'Warming up' for a person, on every surface)" <| fun _ ->
      every
      |> List.filter (fun (state, _, _) -> state <> SessionState.Ready)
      |> List.iter (fun (state, source, freshness) ->
        let expected =
          match state with
          | SessionState.WarmingUp -> "Warming up"
          | SessionState.Uninitialized
          | SessionState.Ready
          | SessionState.Evaluating
          | SessionState.Faulted -> SessionState.label state
        Readiness.decide state source freshness
        |> Readiness.label
        |> Expect.equal (sprintf "%A" state) expected)

    testCase "WHY — over every combination, a build that is behind never reads as failure: red is for failure only" <| fun _ ->
      every
      |> List.filter (fun (state, _, _) -> state = SessionState.Ready)
      |> List.iter (fun (state, source, freshness) ->
        Readiness.decide state source freshness
        |> Readiness.tone
        |> fun tone -> (tone <> ReadinessTone.Failing) |> Expect.isTrue (sprintf "%A / %A is not a failure" source freshness))

    testCase "WHY — only a Faulted session is Failing" <| fun _ ->
      every
      |> List.iter (fun (state, source, freshness) ->
        let failing = Readiness.decide state source freshness |> Readiness.tone = ReadinessTone.Failing
        failing |> Expect.equal (sprintf "%A" state) (state = SessionState.Faulted))

    testCase "WHY — a changed file outranks a rebuild, a rebuild outranks the REPL being behind, and a fact outranks not knowing" <| fun _ ->
      let behind = ReplFreshness.BehindApp (1, [])
      Readiness.decide SessionState.Ready (SourceState.Stale [ staleFile ]) behind |> Expect.equal "stale over behind" Readiness.ReadyOlderCode
      Readiness.decide SessionState.Ready (SourceState.Rebuilding t0) behind |> Expect.equal "rebuild over behind" Readiness.ReadyRebuilding
      Readiness.decide SessionState.Ready (SourceState.Unknown UnknownReason.NotAssessed) behind
      |> Expect.equal "behind over unknown" Readiness.ReadyReplBehindApp

    testCase "WHY — every readiness has its own non-empty words, so no two states read alike" <| fun _ ->
      let readinesses =
        [ for state in everyState do yield Readiness.InState state
          yield Readiness.ReadyCurrent
          yield Readiness.ReadyOlderCode
          yield Readiness.ReadyRebuilding
          yield Readiness.ReadyReplBehindApp
          yield Readiness.ReadyUnchecked ]
      let labels = readinesses |> List.filter (function Readiness.InState SessionState.Ready -> false | _ -> true) |> List.map Readiness.label
      labels |> List.forall (String.IsNullOrWhiteSpace >> not) |> Expect.isTrue "every label says something"
      labels |> List.distinct |> List.length |> Expect.equal "no two labels alike" labels.Length
  ]

// ── The card's short form of the build state ─────────────────────────────────────────────────────────────────────────

let private workingDir = "/home/will/proj"

let private editedIn (path: string) = { Path = path; Because = StaleBecause.EditedAfterBuild (t0.AddMinutes 5.0, t0) }

let private noteOf (state: SourceState) : (string * string list) option =
  match SourceState.cardNote workingDir state with
  | CardNote.NothingToSay -> None
  | CardNote.Note (headline, details) -> Some (headline, details)

[<Tests>]
let cardNoteTests =
  testList "SourceState.cardNote" [

    testCase "WHY — one changed file is one sentence: the session runs older code, and how many files changed" <| fun _ ->
      noteOf (SourceState.Stale [ editedIn "/home/will/proj/src/Tuning.fs" ])
      |> Option.map fst
      |> Expect.equal "the headline is the cause only; the card's pill beside it says the state" (Some "1 file changed since the build.")

    testCase "WHY — the sentence counts files and says 'files' from two on" <| fun _ ->
      noteOf (SourceState.Stale [ editedIn "/home/will/proj/A.fs"; editedIn "/home/will/proj/B.fs"; editedIn "/home/will/proj/C.fs" ])
      |> Option.map fst
      |> Expect.equal "three files" (Some "3 files changed since the build.")

    testCase "WHY — the details name each file relative to the working directory, with when it was edited and when the build is from" <| fun _ ->
      let _, details = noteOf (SourceState.Stale [ editedIn "/home/will/proj/src/Tuning.fs" ]) |> Option.get
      details
      |> Expect.equal "one line, relative" [ "src/Tuning.fs: edited 2026-11-05 16:05:00Z, the build is from 2026-11-05 16:00:00Z" ]

    testCase "WHY — a file outside the working directory shrinks to its file name, never a chain of .." <| fun _ ->
      let _, details = noteOf (SourceState.Stale [ editedIn "/somewhere/else/Other.fs" ]) |> Option.get
      details |> List.head |> Expect.stringStarts "the file name" "Other.fs:"

    testCase "WHY — no absolute path appears anywhere in the note, over every kind of state that has one" <| fun _ ->
      let states =
        [ SourceState.Stale [ editedIn "/home/will/proj/src/A.fs"; editedIn "/elsewhere/B.fs" ]
          SourceState.Stale [ { Path = "/home/will/proj/bin/P.dll"; Because = StaleBecause.RebuiltAfterLoad (t0.AddMinutes 5.0, t0) } ]
          SourceState.Rebuilding t0
          SourceState.Unknown (UnknownReason.Unreadable ("/home/will/proj/src/A.fs", "denied"))
          SourceState.Unknown (UnknownReason.NotInspectable ("/home/will/proj/P.fsproj", "wildcard"))
          SourceState.Unknown (UnknownReason.LoadTimeNotReported "no report") ]
      for state in states do
        match noteOf state with
        | None -> failtest (sprintf "%A has something to say" state)
        | Some (headline, details) ->
          for line in headline :: details do
            line.Contains "/home/will" |> Expect.isFalse (sprintf "%A: %s" state line)
            line.Contains "/elsewhere" |> Expect.isFalse (sprintf "%A: %s" state line)

    testCase "WHY — a build that is current, and a session with no project, say nothing" <| fun _ ->
      noteOf (SourceState.InSync (t0, 3)) |> Expect.isNone "in sync"
      noteOf (SourceState.Unknown UnknownReason.NoProjectLoaded) |> Expect.isNone "no project"

    testCase "WHY — a rebuild and a build that cannot be read are said, because 'could not tell' is not 'fine'" <| fun _ ->
      noteOf (SourceState.Rebuilding t0) |> Option.map fst |> Option.get
      |> Expect.stringStarts "a rebuild" "Rebuilding:"
      noteOf (SourceState.Unknown (UnknownReason.LoadTimeNotReported "no report")) |> Option.map fst
      |> Expect.equal "unknown" (Some "Cannot tell whether this session runs current code.")

    testCase "WHY — a newer build on disk is said without naming a file as 'changed'" <| fun _ ->
      noteOf (SourceState.Stale [ { Path = "/home/will/proj/bin/P.dll"; Because = StaleBecause.RebuiltAfterLoad (t0.AddMinutes 5.0, t0) } ])
      |> Option.map fst
      |> Expect.equal "the headline" (Some "A newer build exists than the one this session loaded.")

    testCase "WHY — a long list is cut after twenty lines and says how many more" <| fun _ ->
      let many = [ for i in 1 .. 25 -> editedIn (sprintf "/home/will/proj/F%d.fs" i) ]
      let headline, details = noteOf (SourceState.Stale many) |> Option.get
      headline |> Expect.stringContains "the sentence counts them all" "25 files"
      details |> List.length |> Expect.equal "twenty and the remainder line" 21
      details |> List.last |> Expect.equal "the remainder" "and 5 more"

    testCase "WHY — the long wording that agents and 'sagefs status' read is untouched by the card's short form" <| fun _ ->
      let state = SourceState.Stale [ editedIn "/home/will/proj/src/A.fs" ]
      SourceState.describe state |> Expect.stringContains "still the long sentence" "STALE SOURCE: 1 file(s) changed on disk after the build this session runs"
      SourceState.describe state |> Expect.stringContains "still the absolute path, which a tool result wants" "/home/will/proj/src/A.fs"
      SourceState.banner state |> Expect.stringStarts "still the warning" "WARNING: STALE SOURCE"
  ]

// ── One phrase per state, and one remedy decision ────────────────────────────────────────────────────────────────────

let private qualified =
  [ Readiness.ReadyOlderCode; Readiness.ReadyRebuilding; Readiness.ReadyReplBehindApp; Readiness.ReadyUnchecked ]

[<Tests>]
let phraseTests =
  testList "Readiness.brief and Remedy" [

    testCase "WHY — the short phrase for a session running older code is the one the pane note, the card and the strip print" <| fun _ ->
      Readiness.brief Readiness.ReadyOlderCode |> Expect.equal "the phrase" (Some "Running older code")

    testCase "WHY — a plain Ready and a state other than Ready have no phrase, because there is nothing to qualify" <| fun _ ->
      Readiness.brief Readiness.ReadyCurrent |> Expect.isNone "plain Ready"
      for state in everyState do
        Readiness.brief (Readiness.InState state) |> Expect.isNone (sprintf "%A" state)

    testCase "WHY — over every qualified readiness the long label is 'Ready, ' and the short phrase, so no surface words one state two ways" <| fun _ ->
      for readiness in qualified do
        let brief = Readiness.brief readiness |> Option.get
        Readiness.label readiness |> fun label -> label.ToLowerInvariant() |> Expect.equal (sprintf "%A" readiness) ("ready, " + brief.ToLowerInvariant())

    testCase "WHY — the remedy is to rebuild exactly when files changed after the build, and never for a rebuild already under way or a build nobody could read" <| fun _ ->
      for source in everySource do
        let expected =
          match source with
          | SourceState.Stale _ -> Remedy.RebuildAndReload
          | SourceState.InSync _
          | SourceState.Rebuilding _
          | SourceState.Unknown _ -> Remedy.NothingToDo
        Remedy.ofSource source |> Expect.equal (sprintf "%A" source) expected

    testCase "WHY — over every source and freshness, a Ready session is offered the rebuild exactly when its readiness is 'running older code'" <| fun _ ->
      for source in everySource do
        for freshness in everyFreshness do
          let olderCode = Readiness.decide SessionState.Ready source freshness = Readiness.ReadyOlderCode
          (Remedy.ofSource source = Remedy.RebuildAndReload) |> Expect.equal (sprintf "%A / %A" source freshness) olderCode
  ]
