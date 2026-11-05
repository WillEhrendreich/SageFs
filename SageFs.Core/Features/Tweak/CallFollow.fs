/// Following a call into its initializer (docs/plans/live-bindings-watch-view.md, phase 3).
///
/// A live binding built by a call (`let player = initPlayer ()`) has parts its own file does not spell: the file says `initPlayer ()`,
/// and the literal behind `Health` sits in the callee. This module finds that literal. The link is the NAME of the callee, matched in
/// the session's own files, and it never guesses: a callee declared once is followed; a callee declared twice, in a file nobody could
/// read, absent, or not read yet is refused, and each is its own answer. A part the callee builds from something other than a literal
/// or a formula (a parameter, another call) has no literal to write and is refused too.
///
/// Every caller of the callee gets its value from the same literal, so a write there changes all of them. The count of callers is the
/// number of applications of that name across the session's files, the row's own call among them.
///
/// Pure. The index is built by the shell that reads the files (`LiveBindingsTweakService`).
module SageFs.Features.Tweak.CallFollow

open System
open System.Collections
open FSharp.Reflection
open Fantomas.FCS.Syntax
open Fantomas.FCS.Text
open SageFs.Features.Tweak.TweakAddress
open SageFs.Features.Tweak.Nudge
open SageFs.Features.Tweak.BindingTweak

// ── reading the files ──

/// Every node of a parsed tree, by reflection over the F# unions, records, tuples and lists it is made of. The tree has no
/// traversal helper, and writing one case by case would miss the constructs added later; this reaches all of them. A node can be
/// reachable by two fields (a lambda holds its body twice), so a caller that counts must not count a node twice.
let rec private descendants (node: obj) : seq<obj> =
  seq {
    match node with
    | null -> ()
    | :? string -> ()
    | :? IEnumerable as items ->
      for item in items do
        yield! descendants item
    | _ ->
      let shape = node.GetType()
      yield node
      if FSharpType.IsUnion(shape, true) then
        let _, fields = FSharpValue.GetUnionFields(node, shape, true)
        for field in fields do
          yield! descendants field
      elif FSharpType.IsRecord(shape, true) then
        for field in FSharpValue.GetRecordFields(node, true) do
          yield! descendants field
      elif FSharpType.IsTuple shape then
        for field in FSharpValue.GetTupleFields node do
          yield! descendants field
  }

let private identsText (ids: Ident list) : string = ids |> List.map _.idText |> String.concat "."

/// The name an application's function is written as, when it is a plain or module-qualified identifier.
let private writtenName (func: SynExpr) : string option =
  match func with
  | SynExpr.Ident id -> Some id.idText
  | SynExpr.LongIdent(longDotId = SynLongIdent(id = ids)) -> Some(identsText ids)
  | _ -> None

let rec private stripped (expr: SynExpr) : SynExpr =
  match expr with
  | SynExpr.Paren(expr = inner)
  | SynExpr.Typed(expr = inner) -> stripped inner
  | other -> other

/// The name a right-hand side applies, as written: `initPlayer ()` gives `initPlayer`, `Game.Tuning.make 1 2` gives
/// `Game.Tuning.make`. None for anything that is not a call of a plain name (a record, an operator, a generic call).
let calleeOf (rhs: string) : string option =
  match parseExpr rhs with
  | Result.Error _ -> None
  | Result.Ok expr ->
    let rec innermost (e: SynExpr) =
      match e with
      | SynExpr.App(isInfix = false; funcExpr = SynExpr.App _ as inner) -> innermost inner
      | SynExpr.App(isInfix = false; funcExpr = func) -> writtenName func
      | _ -> None
    innermost (stripped expr)

/// How many times each name is applied in a file's text, once per call (`make 1 2` is one call, a call inside a lambda counts, and a
/// name passed as a value does not). A file that does not parse is not counted.
let applicationsIn (source: string) : ApplicationCounts =
  match parseInput source with
  | Error(ResolveError.ParseFailed reason) -> ApplicationCounts.NotCounted reason
  | Error(ResolveError.BindingRemoved _)
  | Error(ResolveError.PathGone _) -> ApplicationCounts.NotCounted "the file could not be read"
  | Ok input ->
    descendants (box input)
    |> Seq.choose (function
      | :? SynExpr as expr -> Some expr
      | _ -> None)
    |> Seq.choose (function
      | SynExpr.App(isInfix = false; funcExpr = func) as call -> writtenName func |> Option.map (fun name -> name, call.Range)
      | _ -> None)
    |> Seq.distinct
    |> Seq.countBy fst
    |> Map.ofSeq
    |> ApplicationCounts.Counted

/// The counts of every file added up. One file that could not be counted makes the total not counted, and names the file, because
/// a total over the files that could be read is not the number of callers.
let combineCounts (perFile: (string * ApplicationCounts) list) : ApplicationCounts =
  let failures =
    perFile
    |> List.choose (fun (file, counts) ->
      match counts with
      | ApplicationCounts.NotCounted reason -> Some(sprintf "%s: %s" file reason)
      | ApplicationCounts.Counted _ -> None)
  match failures with
  | first :: _ -> ApplicationCounts.NotCounted first
  | [] ->
    perFile
    |> List.collect (fun (_, counts) ->
      match counts with
      | ApplicationCounts.Counted found -> Map.toList found
      | ApplicationCounts.NotCounted _ -> [])
    |> List.groupBy fst
    |> List.map (fun (name, entries) -> name, entries |> List.sumBy snd)
    |> Map.ofList
    |> ApplicationCounts.Counted

// ── following ──

/// A name as it is written at a call: the modules before it, and the name.
type private Written = { Qualifier: string list; Name: string }

let private writtenOf (text: string) : Written =
  let parts = text.Split('.') |> List.ofArray
  { Qualifier = List.take (parts.Length - 1) parts; Name = List.last parts }

/// A qualifier names a module when it is the module's path or the end of it (`Tuning.make` for `Game.Tuning`). No qualifier names every module.
let private qualifies (written: Written) (modulePath: string list) : bool =
  let skip = List.length modulePath - List.length written.Qualifier
  skip >= 0 && List.skip skip modulePath = written.Qualifier

/// The names the right-hand sides in `wholes` apply, without their modules: the callees the files must be read for.
let calleeNamesOf (wholes: SourceRef list) : Set<string> =
  wholes
  |> List.choose (fun whole -> calleeOf whole.Text)
  |> List.map (fun text -> (writtenOf text).Name)
  |> Set.ofList

/// The bindings whose whole right-hand side is worth reading: those the pane asks about (`names`) that a file builds with a call of
/// a name some owned file declares (`known`). Anything else the door lists no point of stays "not in a file", as before: a call to
/// the framework is not a callee the pane can follow, and its expression is not offered for editing.
let callBuiltToRead (names: Set<string>) (known: Set<string>) (declared: DeclaredBinding list) : TweakAddress list =
  declared
  |> List.filter (fun binding -> Set.contains binding.Name names)
  |> List.filter (fun binding ->
    match calleeOf binding.Rhs with
    | Some text -> Set.contains (writtenOf text).Name known
    | None -> false)
  |> List.map (fun binding -> { ModulePath = binding.ModulePath; BindingName = binding.Name; Path = [] })
  |> List.distinct

let private callersOf (index: SourceIndex) (callee: Written) (modulePath: string list) : CallerCount =
  match index.Applied with
  | ApplicationCounts.NotCounted reason -> CallerCount.NotCounted reason
  | ApplicationCounts.Counted counts ->
    counts
    |> Map.toList
    |> List.filter (fun (text, _) ->
      let written = writtenOf text
      written.Name = callee.Name && qualifies written modulePath)
    |> List.sumBy snd
    |> CallerCount.Counted

/// Follow the call that builds `owner`'s binding to the literal behind the part at `steps`. `owner` is the binding's whole right-hand
/// side as the files spell it. The callee is found in `index` by name, and what is found decides the answer.
let follow (index: SourceIndex) (owner: SourceRef) (steps: PathStep list) : Outcome =
  match calleeOf owner.Text with
  | None -> Outcome.Refused Refusal.NotAPlainCall
  | Some text ->
    let written = writtenOf text
    let declared =
      SourceIndex.declarations index written.Name
      |> List.filter (fun declaration -> qualifies written declaration.ModulePath)
    match declared with
    | [] ->
      match index.Unreadable with
      | [] -> Outcome.Refused(Refusal.NoCalleeInFiles written.Name)
      | _ -> Outcome.Refused(Refusal.CalleeFilesUnknown(SourceIndex.unreadableReason index))
    | _ ->
      match declared |> List.filter (fun declaration -> List.isEmpty declaration.Owners) with
      | unread :: _ -> Outcome.Refused(Refusal.CalleeNotRead(written.Name, unread.File))
      | [] ->
        match declared |> List.collect (fun declaration -> declaration.Owners) with
        | [] -> Outcome.Refused(Refusal.NoCalleeInFiles written.Name)
        | _ :: _ :: _ as several -> Outcome.Refused(Refusal.CalleeDeclaredSeveralTimes(written.Name, several))
        | [ callee ] ->
          let part =
            index.Files
            |> List.filter (fun file -> String.Equals(file.File, callee.File, StringComparison.Ordinal))
            |> List.collect (fun file -> file.Items)
            |> List.filter (fun item ->
              item.Address.ModulePath = callee.Address.ModulePath
              && item.Address.BindingName = written.Name
              && item.Address.Path = steps)
          match part with
          | [ place ] ->
            match place.Kind with
            | ItemKind.Knob _ ->
              Outcome.Followed
                { Callee = written.Name
                  Place = place
                  Callers = callersOf index written callee.Address.ModulePath }
            | ItemKind.Formula -> Outcome.Refused(Refusal.ResultIsAnExpression place)
          | _ -> Outcome.Refused(Refusal.ResultNotSpelled callee)
