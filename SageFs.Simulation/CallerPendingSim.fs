namespace SageFs.Simulation

open SageFs.Features.CallerState
open SageFs.Features.CallerCheck
open SageFs.Features.ReloadPlanning

/// Deterministic Simulation Testing for the claim a save of a re-signed function makes about its callers in other files.
///
/// A project has a library file and two caller files. A save of the library re-signs, removes, renames or edits the body
/// of a function; a save of a caller file moves the calls in it onto the library as it stands; a caller file can be
/// saved with no edit, saved broken (it does not compile, so it does not land), or the app can restart. The decision folded
/// is the REAL one: the planner's `subjectsOf` over real parsed text, `CallerCheck.decide` over the callers' text on disk,
/// and the real `CallerLedger`.
///
/// Same design rules as `PatchConfirmationSim`:
///   * Chaos is DATA: a `Scenario` is an ordered op list. Same ops, same trace.
///   * Ground truth is an independent fold (`Gen` and `Bound`) that never reads the ledger: how many times each
///     library function's method was replaced or removed, and which generation each caller's landed code was bound to.
///     A caller is stranded when it still calls a function whose method was replaced after the caller last landed.
///   * Twins reintroduce the bugs the invariants exist to catch.
module CallerPendingSim =

  [<Literal>]
  let libPath = "/sim/Lib.fs"

  let callerPath (index: int) : string = sprintf "/sim/C%d.fs" index

  [<RequireQualifiedAccess>]
  type Op =
    /// The library's function (0 or 1) gains a parameter: a new method.
    | ResignLib of fn: int
    /// Its body changes and its header does not: the same method.
    | BodyEditLib of fn: int
    /// It is deleted from the library.
    | RemoveLib of fn: int
    /// It is renamed (and renamed back if it already was): the old name is removed.
    | RenameLib of fn: int
    /// Caller file 1 or 2 is edited so it compiles against the library as it stands, and saved: it lands.
    | SaveCaller of file: int
    /// The caller file is saved with no edit: nothing lands.
    | SaveCallerUnchanged of file: int
    /// The caller file is saved with an edit that does not compile: nothing lands.
    | SaveCallerBroken of file: int
    /// The app restarts from a build that compiles: every caller is current.
    | Restart

  type Scenario = { Seed: int; Ops: Op list }

  type LibFn = { Name: string; Arity: int; Body: int }

  type CallerFile =
    { Index: int
      /// The library functions the caller is written to call (an index into the library).
      Wants: int list
      /// What is on disk: what a scan of the other files reads.
      Disk: string
      /// What the last text that LANDED calls, as (name, arity): what the running process runs.
      Landed: (string * int) list
      /// The calls the disk text makes.
      DiskCalls: (string * int) list
      Edits: int }

  type World =
    { Lib: Map<int, LibFn>
      /// The library's text as it last landed: the baseline the next save is read against.
      LibLanded: string
      Callers: Map<int, CallerFile>
      Ledger: CallerLedger
      /// Truth: how many times each library name's method was replaced or removed.
      Gen: Map<string, int>
      /// Truth: the generation a caller's landed call was bound to, by (caller, name).
      Bound: Map<int * string, int> }

  /// One op's end: what the ledger says it would report, against the truth.
  type Snapshot =
    { Op: Op
      Reported: CallersState
      /// Truth: (caller file, library declaration) pairs still on an old method.
      Stranded: Set<string * string>
      /// What the ledger lists: the same pairs.
      Listed: Set<string * string> }

  type Trace =
    { Scenario: Scenario
      Snapshots: Snapshot list
      Reducer: string }

  [<RequireQualifiedAccess>]
  type private Policy =
    /// The real behaviour.
    | Real
    /// TWIN: a save records nothing about callers, as every save did before this state existed.
    | DropsTheCheck
    /// TWIN: any caller landing clears everything pending, not just its own calls.
    | ClearsOnAnySave
    /// TWIN: a landing never clears anything.
    | NeverClears

  let private declaredIn (name: string) : string = "Sim.Lib." + name

  let private arguments (arity: int) : string = String.concat "" [ for _ in 1..arity -> " 1" ]

  let private renderFn (f: LibFn) : string =
    let parameters = String.concat " " [ for i in 0 .. f.Arity - 1 -> sprintf "(p%d: int)" i ]
    let sum = String.concat " + " ([ for i in 0 .. f.Arity - 1 -> sprintf "p%d" i ] @ [ string f.Body ])
    sprintf "let %s %s : int = %s" f.Name parameters sum

  let private libText (fns: Map<int, LibFn>) : string =
    "module Sim.Lib\n\n" + (fns |> Map.toList |> List.map (snd >> renderFn) |> String.concat "\n\n") + "\n"

  let private callerText (index: int) (calls: (string * int) list) (edits: int) : string =
    let lines = calls |> List.mapi (fun i (name, arity) -> sprintf "  let r%d = Sim.Lib.%s%s" i name (arguments arity))
    sprintf "module Sim.C%d\n\nlet c%d () : int =\n%s\n  %d\n" index index (String.concat "\n" lines) edits

  let private callerDecl (index: int) : string = sprintf "Sim.C%d.c%d" index index

  let private initialFns : Map<int, LibFn> =
    Map.ofList [ 0, { Name = "f0"; Arity = 1; Body = 0 }; 1, { Name = "f1"; Arity = 1; Body = 0 } ]

  let private initialCaller (index: int) (wants: int list) : CallerFile =
    let calls = wants |> List.map (fun w -> initialFns.[w].Name, initialFns.[w].Arity)
    { Index = index; Wants = wants; Disk = callerText index calls 0; Landed = calls; DiskCalls = calls; Edits = 0 }

  let private initial : World =
    { Lib = initialFns
      LibLanded = libText initialFns
      Callers = Map.ofList [ 1, initialCaller 1 [ 0 ]; 2, initialCaller 2 [ 1 ] ]
      Ledger = CallerLedger.empty
      Gen = Map.empty
      Bound = Map.ofList [ (1, "f0"), 0; (2, "f1"), 0 ] }

  let private declsOf (text: string) : FileDecls =
    match extractDecls text with
    | Ok decls -> decls
    | Error reason -> failwithf "the sim wrote text that does not parse (%s):\n%s" reason text

  let private gen (w: World) (name: string) : int = Map.tryFind name w.Gen |> Option.defaultValue 0

  let private othersOf (w: World) : OtherSources =
    OtherSources.Loaded [ for KeyValue(i, c) in w.Callers -> { Path = callerPath i; Text = c.Disk } ]

  /// The sim reads by name: it has no project to hand the compiler, and a name match is exact over these texts.
  let private noCompiler = CompilerAnswer.Unavailable NameOnlyReason.NoProjectOptions

  /// A save of the library. Everything it re-signed or removed is checked against the callers' text on disk.
  let private libSaved (policy: Policy) (w: World) (fns: Map<int, LibFn>) (bumped: string list) : World =
    let text = libText fns
    let subjects = subjectsOf libPath (declsOf w.LibLanded) (declsOf text)
    let checks = decide (othersOf w) noCompiler subjects
    let landed = LedgerEvent.FileLanded(libPath, [])
    let ledger =
      match policy with
      | Policy.NeverClears -> w.Ledger
      | Policy.Real
      | Policy.DropsTheCheck
      | Policy.ClearsOnAnySave -> CallerLedger.apply landed w.Ledger
    let ledger =
      match policy with
      | Policy.DropsTheCheck -> ledger
      | Policy.Real
      | Policy.ClearsOnAnySave
      | Policy.NeverClears -> CallerLedger.apply (LedgerEvent.Checked checks) ledger
    let gens = bumped |> List.fold (fun g name -> Map.add name ((Map.tryFind name g |> Option.defaultValue 0) + 1) g) w.Gen
    { w with Lib = fns; LibLanded = text; Ledger = ledger; Gen = gens }

  let private callsFor (fns: Map<int, LibFn>) (wants: int list) : (string * int) list =
    wants |> List.choose (fun w -> Map.tryFind w fns |> Option.map (fun f -> f.Name, f.Arity))

  /// A save of a caller file that compiles and lands: its calls move onto the library as it stands.
  let private callerLanded (policy: Policy) (w: World) (index: int) : World =
    let c = w.Callers.[index]
    let calls = callsFor w.Lib c.Wants
    let edits = c.Edits + 1
    let text = callerText index calls edits
    let subjects = subjectsOf (callerPath index) (declsOf c.Disk) (declsOf text)
    let checks = decide (othersOf w) noCompiler subjects
    let landed =
      match policy with
      | Policy.ClearsOnAnySave -> LedgerEvent.AppRestarted
      | Policy.Real
      | Policy.DropsTheCheck
      | Policy.NeverClears -> LedgerEvent.FileLanded(callerPath index, [ callerDecl index ])
    let ledger =
      match policy with
      | Policy.NeverClears -> w.Ledger
      | Policy.Real
      | Policy.DropsTheCheck
      | Policy.ClearsOnAnySave -> CallerLedger.apply landed w.Ledger
    let ledger =
      match policy with
      | Policy.DropsTheCheck -> ledger
      | Policy.Real
      | Policy.ClearsOnAnySave
      | Policy.NeverClears -> CallerLedger.apply (LedgerEvent.Checked checks) ledger
    let bound = calls |> List.fold (fun b (name, _) -> Map.add (index, name) (gen w name) b) w.Bound
    let caller = { c with Disk = text; Landed = calls; DiskCalls = calls; Edits = edits }
    { w with Callers = Map.add index caller w.Callers; Ledger = ledger; Bound = bound }

  let private present (w: World) (fn: int) : LibFn option = Map.tryFind fn w.Lib

  let private step (policy: Policy) (w: World) (op: Op) : World =
    match op with
    | Op.ResignLib fn ->
      match present w fn with
      | Some f -> libSaved policy w (Map.add fn { f with Arity = f.Arity + 1 } w.Lib) [ f.Name ]
      | None -> w
    | Op.BodyEditLib fn ->
      match present w fn with
      | Some f -> libSaved policy w (Map.add fn { f with Body = f.Body + 1 } w.Lib) []
      | None -> w
    | Op.RemoveLib fn ->
      match present w fn with
      | Some f -> libSaved policy w (Map.remove fn w.Lib) [ f.Name ]
      | None -> w
    | Op.RenameLib fn ->
      match present w fn with
      | Some f ->
        let renamed = match f.Name.StartsWith "f" with true -> "g" + f.Name.Substring 1 | false -> "f" + f.Name.Substring 1
        libSaved policy w (Map.add fn { f with Name = renamed } w.Lib) [ f.Name ]
      | None -> w
    | Op.SaveCaller file -> callerLanded policy w file
    | Op.SaveCallerUnchanged _ -> w
    | Op.SaveCallerBroken file ->
      let c = w.Callers.[file]
      // The text changed and does not compile: the disk has it, the process does not.
      { w with Callers = Map.add file { c with Disk = callerText file c.DiskCalls (c.Edits + 1) + "// does not compile\n" } w.Callers }
    | Op.Restart ->
      let callers =
        w.Callers
        |> Map.map (fun i c ->
          let calls = callsFor w.Lib c.Wants
          { c with Disk = callerText i calls (c.Edits + 1); Landed = calls; DiskCalls = calls; Edits = c.Edits + 1 })
      let bound =
        callers
        |> Map.toList
        |> List.collect (fun (i, c) -> c.Landed |> List.map (fun (name, _) -> (i, name), gen w name))
        |> Map.ofList
      { w with Callers = callers; Bound = bound; LibLanded = libText w.Lib; Ledger = CallerLedger.apply LedgerEvent.AppRestarted w.Ledger }

  /// Truth: a caller is stranded on a function when its landed code still calls it and the function's method was replaced
  /// or removed after that code was bound.
  let private stranded (w: World) : Set<string * string> =
    set
      [ for KeyValue(i, c) in w.Callers do
          for name, _ in c.Landed do
            let boundAt = Map.tryFind (i, name) w.Bound |> Option.defaultValue 0
            match boundAt < gen w name with
            | true -> yield callerPath i, declaredIn name
            | false -> () ]

  let private listed (state: CallersState) : Set<string * string> =
    match state with
    | CallersState.CallersPending(first, rest, _) ->
      set [ for p in first :: rest do
              for s in p.First :: p.Rest do
                yield s.File, p.Edit.Declaration ]
    | CallersState.CallersCurrent
    | CallersState.CallersNotChecked _
    | CallersState.CallersNotReported -> Set.empty

  let private snapshot (op: Op) (w: World) : Snapshot =
    let reported = CallerLedger.stateOf w.Ledger
    { Op = op; Reported = reported; Stranded = stranded w; Listed = listed reported }

  let private runWith (name: string) (policy: Policy) (scenario: Scenario) : Trace =
    let _, snapshots =
      scenario.Ops
      |> List.fold
        (fun (w, acc) op ->
          let w' = step policy w op
          w', snapshot op w' :: acc)
        (initial, [])
    { Scenario = scenario; Snapshots = List.rev snapshots; Reducer = name }

  /// The real ledger over the real planner.
  let run (scenario: Scenario) : Trace = runWith "real (CallerLedger over subjectsOf and decide)" Policy.Real scenario

  /// TWIN: records nothing about callers.
  let runDropsTheCheck (scenario: Scenario) : Trace = runWith "twin-drops-the-check" Policy.DropsTheCheck scenario

  /// TWIN: any caller landing clears everything.
  let runClearsOnAnySave (scenario: Scenario) : Trace = runWith "twin-clears-on-any-save" Policy.ClearsOnAnySave scenario

  /// TWIN: a landing never clears.
  let runNeverClears (scenario: Scenario) : Trace = runWith "twin-never-clears" Policy.NeverClears scenario
