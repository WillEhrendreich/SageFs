module SageFs.Tests.ReloadPlanningTests

open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.Features.ReloadPlanning

let private baselineSource = """// header comment
module Demo.Web.Program

open System

type TodoItem = { Id: int; Text: string }

let mutable todos: TodoItem list = []

let render (items: TodoItem list) =
  sprintf "%d remaining" items.Length

let getHome : string = "home"

[<EntryPoint>]
let main args =
  0
"""

let private declsOf (source: string) =
  match extractDecls source with
  | Ok decls -> decls
  | Error reason -> failtestf "extractDecls failed: %s" reason

/// Line endings in the fixture follow the checkout (CRLF on Windows), while
/// edit targets are written with "\n" — compare and edit in LF so an edit can
/// never silently miss (the planner itself is line-ending agnostic).
let private lf (text: string) = text.Replace("\r\n", "\n")

let private replace (oldText: string) (newText: string) (source: string) =
  let source, oldText, newText = lf source, lf oldText, lf newText
  match source.Contains oldText with
  | true -> source.Replace(oldText, newText)
  | false -> failtestf "fixture edit target not found: %s" oldText

let private plan (edited: string) =
  planReload (declsOf baselineSource) (declsOf edited)

let private patchedNames (p: ReloadPlan) =
  match p with
  | ReloadPlan.PatchFunctions fs -> fs |> List.map _.Name
  | ReloadPlan.RestartRequired (first, rest) -> failtestf "expected a patch, got restart %A" (first :: rest)

let private restartChanges (p: ReloadPlan) =
  match p with
  | ReloadPlan.RestartRequired (first, rest) -> first :: rest
  | ReloadPlan.PatchFunctions fs -> failtestf "expected a restart, got patch %A" (fs |> List.map _.Name)

[<Tests>]
let extractDeclsTests =
  testList "ReloadPlanning extractDecls" [
    testCase "WHY — ReloadPlanning.extractDecls — reads the module path and each declaration's kind because the reload decision is made per declaration" <| fun _ ->
      let decls = declsOf baselineSource
      decls.ModulePath |> Expect.equal "file-level module path" [ "Demo"; "Web"; "Program" ]
      decls.Opens |> Expect.equal "opens, for re-emitting" [ "System" ]
      decls.Decls
      |> List.map (fun d -> d.Name, d.Kind)
      |> Expect.equal "kinds in source order"
        [ "TodoItem", DeclKind.TypeDecl
          "todos", DeclKind.ValueDecl
          "render", DeclKind.FunctionDecl
          "getHome", DeclKind.ValueDecl
          "main", DeclKind.EntryPointDecl ]
  ]

[<Tests>]
let planReloadTests =
  testList "ReloadPlanning planReload" [
    testCase "WHY — ReloadPlanning.planReload — an unchanged file patches nothing because a save with no code change must not disturb the app" <| fun _ ->
      plan baselineSource |> patchedNames |> Expect.isEmpty "nothing to patch"

    testCase "WHY — ReloadPlanning.planReload — a comment-only edit patches nothing because comments do not change behaviour" <| fun _ ->
      plan (replace "// header comment" "// a different comment" baselineSource)
      |> patchedNames |> Expect.isEmpty "nothing to patch"

    testCase "WHY — ReloadPlanning.planReload — a function body edit patches just that function because it can be swapped in place" <| fun _ ->
      plan (replace "\"%d remaining\"" "\"%d left to do\"" baselineSource)
      |> patchedNames |> Expect.equal "only render" [ "render" ]

    testCase "WHY — ReloadPlanning.planReload — a type edit requires a restart because the running app's values have the old shape" <| fun _ ->
      plan (replace "Text: string }" "Text: string; Done: bool }" baselineSource)
      |> restartChanges |> Expect.equal "TodoItem changed" [ ReloadChange.TypeChanged "TodoItem" ]

    testCase "WHY — ReloadPlanning.planReload — a module-level value edit requires a restart because the app captured the old value at startup" <| fun _ ->
      plan (replace "\"home\"" "\"welcome\"" baselineSource)
      |> restartChanges |> Expect.equal "getHome changed" [ ReloadChange.ValueChanged "getHome" ]

    testCase "WHY — ReloadPlanning.planReload — a function signature edit requires a restart because callers were compiled against the old one" <| fun _ ->
      plan (replace "let render (items: TodoItem list) =" "let render (title: string) (items: TodoItem list) =" baselineSource)
      |> restartChanges |> Expect.equal "render signature changed" [ ReloadChange.SignatureChanged "render" ]

    testCase "WHY — ReloadPlanning.planReload — an entry point edit requires a restart because it only runs at startup" <| fun _ ->
      plan (replace "  0\n" "  1\n" baselineSource)
      |> restartChanges |> Expect.equal "main changed" [ ReloadChange.EntryPointChanged ]

    testCase "WHY — ReloadPlanning.planReload — removing a function requires a restart because running code may still call it" <| fun _ ->
      plan (replace "let render (items: TodoItem list) =\n  sprintf \"%d remaining\" items.Length\n" "" baselineSource)
      |> restartChanges |> Expect.equal "render removed" [ ReloadChange.DeclarationRemoved "render" ]

    testCase "WHY — ReloadPlanning.planReload — a new function is patched in because nothing compiled references it yet" <| fun _ ->
      plan (baselineSource + "\nlet helper (x: int) = x + 1\n")
      |> patchedNames |> Expect.equal "helper added" [ "helper" ]

    testCase "WHY — ReloadPlanning.planReload — every restart reason is reported because the card must say what changed" <| fun _ ->
      baselineSource
      |> replace "Text: string }" "Text: string; Done: bool }"
      |> replace "\"home\"" "\"welcome\""
      |> plan
      |> restartChanges
      |> Expect.equal "both reasons, source order" [ ReloadChange.TypeChanged "TodoItem"; ReloadChange.ValueChanged "getHome" ]
  ]

[<Tests>]
let realSourceTests =
  testList "ReloadPlanning real sources" [
    testCase "WHY — ReloadPlanning — every Core source file with one top-level module plans against itself as a no-op because an unchanged save must never restart a running app" <| fun _ ->
      let coreDir =
        System.IO.Path.GetFullPath(System.IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "SageFs.Core"))
      let results =
        System.IO.Directory.GetFiles(coreDir, "*.fs", System.IO.SearchOption.AllDirectories)
        |> Array.filter (fun f -> not (f.Contains "/obj/" || f.Contains "\\obj\\"))
        |> Array.map (fun f -> f, extractDecls (System.IO.File.ReadAllText f))
      let extracted =
        results |> Array.choose (fun (f, r) -> match r with Ok d -> Some (f, d) | Error _ -> None)
      extracted.Length > 50 |> Expect.isTrue (sprintf "most Core files have one top-level module (%d did)" extracted.Length)
      extracted
      |> Array.choose (fun (f, d) ->
        let file = System.IO.Path.GetFileName f
        match planReload d d with
        | ReloadPlan.PatchFunctions [] -> None
        | ReloadPlan.PatchFunctions patched -> Some (sprintf "%s patches %A" file (patched |> List.map _.Name))
        | ReloadPlan.RestartRequired (first, rest) -> Some (sprintf "%s restarts %A" file (first :: rest)))
      |> Array.toList
      |> Expect.equal "files that do not plan against themselves as a no-op" []
  ]

[<Tests>]
let companionModuleTests =
  let source = "module Demo.State\n\ntype Phase = Idle | Busy\n\nmodule Phase =\n  let label (p: Phase) = string p\n"
  testList "ReloadPlanning companion modules" [
    testCase "WHY — ReloadPlanning.planReload — a type and its same-named companion module plan against themselves as a no-op because sharing a name is not a change" <| fun _ ->
      planReload (declsOf source) (declsOf source) |> patchedNames |> Expect.isEmpty "nothing to patch"

    testCase "WHY — ReloadPlanning.planReload — editing only the companion module reports the module, not the type, because the card must name what really changed" <| fun _ ->
      let edited = replace "string p" "sprintf \"%A\" p" source
      planReload (declsOf source) (declsOf edited)
      |> restartChanges |> Expect.equal "only the module" [ ReloadChange.ModuleChanged "Phase" ]
  ]

[<Tests>]
let changeWordingTests =
  testList "ReloadPlanning change wording" [
    testCase "WHY — ReloadChange.describeAll — reads every reason as one sentence because the card has one line for it" <| fun _ ->
      ReloadChange.describeAll
        (ReloadChange.TypeChanged "TodoItem")
        [ ReloadChange.SignatureChanged "render"; ReloadChange.DeclarationRemoved "old"; ReloadChange.EntryPointChanged ]
      |> Expect.equal "joined in order" "type TodoItem changed; the signature of render changed; old was removed; the entry point changed"
  ]

[<Tests>]
let confirmPatchTests =
  let before = declsOf baselineSource
  let render = before.Decls |> List.find (fun d -> d.Name = "render")
  let helper =
    { render with Name = "helper"; Header = "let helper (x: int)"; Text = "let helper (x: int) = x + 1" }
  testList "ReloadPlanning confirmPatch" [
    testCase "WHY — ReloadPlanning.confirmPatch — a patched function that was detoured is applied because the running app now calls the new body" <| fun _ ->
      confirmPatch before [ render ] [ "Demo.Web.Program.render" ]
      |> Expect.equal "applied" PatchOutcome.Applied

    testCase "WHY — ReloadPlanning.confirmPatch — a new function needs no detour because nothing compiled calls it yet" <| fun _ ->
      confirmPatch before [ helper ] []
      |> Expect.equal "applied" PatchOutcome.Applied

    testCase "WHY — ReloadPlanning.confirmPatch — an existing function that was not detoured requires a restart because its compiled signature changed" <| fun _ ->
      confirmPatch before [ render ] []
      |> Expect.equal "restart for render" (PatchOutcome.RestartNeeded (ReloadChange.SignatureChanged "render", []))

    testCase "WHY — ReloadPlanning.confirmPatch — a detour of a same-suffixed method does not count because prerender is not render" <| fun _ ->
      confirmPatch before [ render ] [ "Demo.Web.Program.prerender" ]
      |> Expect.equal "restart for render" (PatchOutcome.RestartNeeded (ReloadChange.SignatureChanged "render", []))
  ]

[<Tests>]
let baselineIsTrustworthyTests =
  let epoch = System.DateTime(2026, 1, 1, 0, 0, 0, System.DateTimeKind.Utc)
  testList "ReloadPlanning baselineIsTrustworthy" [
    testCase "WHY — ReloadPlanning.baselineIsTrustworthy — a source file untouched since the build is a trustworthy baseline because it is exactly what the loaded assembly compiled from" <| fun _ ->
      baselineIsTrustworthy epoch epoch
      |> Expect.isTrue "source written at the same instant as the build is trustworthy"

    testCase "WHY — ReloadPlanning.baselineIsTrustworthy — a source file written before the build is trustworthy because the build necessarily read it as-is" <| fun _ ->
      baselineIsTrustworthy epoch (epoch - System.TimeSpan.FromMinutes 5.0)
      |> Expect.isTrue "source older than the build is trustworthy"

    testCase "WHY — ReloadPlanning.baselineIsTrustworthy — a source file edited after the build is NOT trustworthy because capturing it as \"baseline\" would silently absorb an edit the running assembly never saw, and planReload would then see current = baseline and never surface it" <| fun _ ->
      baselineIsTrustworthy epoch (epoch + System.TimeSpan.FromSeconds 1.0)
      |> Expect.isFalse "source newer than the build must not be trusted as the baseline"
  ]

[<Tests>]
let accessTests =
  let source =
    "module Demo.Access\n\ntype internal Hidden = { V: int }\n\nlet private secret () = 41\n\nlet answer () =\n  secret () + 1\n\nlet shout (s: string) = s.ToUpper()\n"
  let accessOf (name: string) = (declsOf source).Decls |> List.find (fun d -> d.Name = name) |> _.Access
  testList "ReloadPlanning access" [
    testCase "WHY — ReloadPlanning.extractDecls — records each declaration's access because a patch compiled outside the assembly cannot see private or internal members" <| fun _ ->
      [ accessOf "Hidden"; accessOf "secret"; accessOf "answer" ]
      |> Expect.equal "internal, private, public" [ DeclAccess.Internal; DeclAccess.Private; DeclAccess.Public ]

    testCase "WHY — ReloadPlanning.planReload — a changed function that uses a private member restarts because the patch would not compile" <| fun _ ->
      planReload (declsOf source) (declsOf (replace "secret () + 1" "secret () + 2" source))
      |> restartChanges
      |> Expect.equal "answer uses secret" [ ReloadChange.UsesNonPublicMember ("answer", "secret") ]

    testCase "WHY — ReloadPlanning.planReload — a changed function that uses only public members is patched because FSI can compile it" <| fun _ ->
      planReload (declsOf source) (declsOf (replace "s.ToUpper()" "s.ToLower()" source))
      |> patchedNames
      |> Expect.equal "shout" [ "shout" ]

    testCase "WHY — ReloadChange.describe — names the function and the member it cannot reach because the card must say why the app restarted" <| fun _ ->
      ReloadChange.describe (ReloadChange.UsesNonPublicMember ("answer", "secret"))
      |> Expect.equal "wording" "answer uses secret, which is not public, so it cannot be patched in place"
  ]

[<Tests>]
let hiddenViaMembersTests =
  let unionSource =
    "module Demo.Shapes\n\ntype internal Shape =\n  | Circle of radius: float\n  | Square of side: float\n\nlet describe () =\n  Circle 1.0\n"
  let recordSource =
    "module Demo.Config\n\ntype internal Config = { Timeout: int }\n\nlet build () =\n  { Timeout = 5 }\n"
  let commentSource =
    "module Demo.Comment\n\nlet private secret () = 41\n\nlet answer () =\n  // secret is mentioned only here, never called\n  \"the word secret in a string\"\n  99\n"
  testList "ReloadPlanning access — case, field and prose uses" [
    testCase "WHY — ReloadPlanning.planReload — a patch that constructs a hidden type's union case restarts because the case never spells the type's own name" <| fun _ ->
      planReload (declsOf unionSource) (declsOf (replace "Circle 1.0" "Circle 2.0" unionSource))
      |> restartChanges
      |> Expect.equal "describe uses Shape via its case" [ ReloadChange.UsesNonPublicMember ("describe", "Shape") ]

    testCase "WHY — ReloadPlanning.planReload — a patch that builds a hidden record type's literal restarts because the literal never spells the type's own name" <| fun _ ->
      planReload (declsOf recordSource) (declsOf (replace "{ Timeout = 5 }" "{ Timeout = 6 }" recordSource))
      |> restartChanges
      |> Expect.equal "build uses Config via its field" [ ReloadChange.UsesNonPublicMember ("build", "Config") ]

    testCase "WHY — ReloadPlanning.planReload — a hidden name that appears only in a comment or string literal is patched because prose is not a reference" <| fun _ ->
      planReload (declsOf commentSource) (declsOf (replace "99" "100" commentSource))
      |> patchedNames
      |> Expect.equal "answer" [ "answer" ]
  ]

[<Tests>]
let exactSymbolResolutionTests =
  testList "ReloadPlanning access — exact FCS symbol resolution, not name matching" [
    testCase "WHY — ReloadPlanning.planReload — a function parameter that shadows a private module value does not force a restart because the patch never touches the private value" <| fun _ ->
      let source = "module Demo.Shadow\n\nlet private timeout = 30\n\nlet describe (timeout: int) =\n  sprintf \"waiting %d\" timeout\n"
      planReload (declsOf source) (declsOf (replace "\"waiting %d\"" "\"wait %d\"" source))
      |> patchedNames
      |> Expect.equal "describe patches — its own parameter shadows the private value, it never references it" [ "describe" ]

    testCase "WHY — ReloadPlanning.planReload — a local let that shadows a private module value does not force a restart because the patch never touches the private value" <| fun _ ->
      let source = "module Demo.Shadow2\n\nlet private secret = 41\n\nlet answer () =\n  let secret = 99\n  secret + 1\n"
      planReload (declsOf source) (declsOf (replace "secret + 1" "secret + 2" source))
      |> patchedNames
      |> Expect.equal "answer patches — its own local `secret` shadows the private value, it never references it" [ "answer" ]

    testCase "WHY — ReloadPlanning.planReload — a genuine reference to a private module value still restarts because exact resolution is not weaker than the identifier heuristic" <| fun _ ->
      let source = "module Demo.Shadow3\n\nlet private secret = 41\n\nlet answer () =\n  secret + 1\n"
      planReload (declsOf source) (declsOf (replace "secret + 1" "secret + 2" source))
      |> restartChanges
      |> Expect.equal "answer genuinely uses the private secret" [ ReloadChange.UsesNonPublicMember ("answer", "secret") ]

    testCase "WHY — ReloadPlanning.planReload — when the file cannot be type-checked standalone (an unresolvable open, the common case for a real app with NuGet/ASP.NET references), a name that only shadows a private value still forces a restart because an incomplete symbol table must never be trusted to say \"unused\"" <| fun _ ->
      // `open Unresolvable.Namespace.Does.Not.Exist` parses fine (Fantomas only
      // checks syntax) but fails FCS's standalone type-check with FS0039 — so
      // `symbolUsesOf` returns Error and `unreachableOf` must fall back to the
      // identifier-set heuristic, which is fail-closed toward restart.
      let source =
        "module Demo.Shadow4\n\nopen Unresolvable.Namespace.Does.Not.Exist\n\nlet private secret = 41\n\nlet describe (secret: int) =\n  sprintf \"got %d\" secret\n"
      planReload (declsOf source) (declsOf (replace "\"got %d\"" "\"have %d\"" source))
      |> restartChanges
      |> Expect.equal
        "fallback flags describe as using secret by name — even though it only shadows the private value — because the exact check could not run"
        [ ReloadChange.UsesNonPublicMember ("describe", "secret") ]
  ]

// ── Planner laws over generated declaration sets ──

let private namePool = [ "alpha"; "beta"; "gamma"; "delta"; "epsilon"; "zeta"; "eta"; "theta" ]

let private allKinds =
  [ DeclKind.TypeDecl; DeclKind.ValueDecl; DeclKind.FunctionDecl
    DeclKind.EntryPointDecl; DeclKind.NestedModuleDecl; DeclKind.StartupCode ]

/// A body that may mention other declarations by name, so the
/// non-public-member rule is exercised.
let private genBody =
  gen {
    let! a = Gen.elements ("x" :: "0" :: namePool)
    let! op = Gen.elements [ "+"; "*"; "-" ]
    let! b = Gen.elements ("1" :: "x" :: namePool)
    return sprintf "%s %s %s" a op b
  }

let private mkDecl (name: string) (kind: DeclKind) (access: DeclAccess) (body: string) : SourceDecl =
  let accessText =
    match access with
    | DeclAccess.Public -> ""
    | DeclAccess.Internal -> "internal "
    | DeclAccess.Private -> "private "
  let header, text =
    match kind with
    | DeclKind.FunctionDecl -> sprintf "let %s%s x" accessText name, sprintf "let %s%s x = %s" accessText name body
    | DeclKind.ValueDecl -> sprintf "let %s%s" accessText name, sprintf "let %s%s = %s" accessText name body
    | DeclKind.EntryPointDecl -> sprintf "let %s args" name, sprintf "[<EntryPoint>]\nlet %s args = %s" name body
    | DeclKind.TypeDecl -> "", sprintf "type %s%s = { Value: int } // %s" accessText name body
    | DeclKind.NestedModuleDecl -> "", sprintf "module %s%s =\n  let inner = %s" accessText name body
    | DeclKind.StartupCode -> "", sprintf "printfn \"%%d\" (%s)" body
  { Name = name; Kind = kind; Access = access; Header = header; Text = text; StartLine = 1; EndLine = 1 }

let private genDeclNamed (name: string) =
  gen {
    let! kind = Gen.elements allKinds
    let! access =
      Gen.frequency [
        4, Gen.constant DeclAccess.Public
        1, Gen.constant DeclAccess.Internal
        1, Gen.constant DeclAccess.Private ]
    let! body = genBody
    return mkDecl name kind access body
  }

// RawSource = None: these files are synthetic SourceDecl lists built directly,
// not parsed by extractDecls, so there is no real text for the FCS-exact path
// to check — planReload falls back to the identifier-set heuristic for them,
// which is exactly what these generated-declaration-set laws exercise.
let private fileOf (decls: SourceDecl list) = { ModulePath = [ "Demo"; "App" ]; Opens = [ "System" ]; Decls = decls; RawSource = None }

/// Every declaration has its own name.
let private genUniqueFile =
  gen {
    let! count = Gen.choose (1, namePool.Length)
    let! decls = namePool |> List.truncate count |> Gen.collectToList genDeclNamed
    return fileOf decls
  }

/// Names may repeat (shadowing, a type and its companion module).
let private genFileWithRepeats =
  gen {
    let! count = Gen.choose (0, 10)
    let! names = Gen.listOfLength count (Gen.elements namePool)
    let! decls = names |> Gen.collectToList genDeclNamed
    return fileOf decls
  }

/// A genuine edit to a declaration's code; a function keeps its header.
let private edit (d: SourceDecl) = { d with Text = d.Text + " + 7" }

let private reasonsOf (plan: ReloadPlan) =
  match plan with
  | ReloadPlan.PatchFunctions _ -> Set.empty
  | ReloadPlan.RestartRequired (first, rest) -> Set.ofList (first :: rest)

/// What editing a declaration other than a function body must report.
let private expectedChangeFor (d: SourceDecl) =
  match d.Kind with
  | DeclKind.TypeDecl -> ReloadChange.TypeChanged d.Name
  | DeclKind.ValueDecl -> ReloadChange.ValueChanged d.Name
  | DeclKind.FunctionDecl -> ReloadChange.SignatureChanged d.Name
  | DeclKind.EntryPointDecl -> ReloadChange.EntryPointChanged
  | DeclKind.NestedModuleDecl -> ReloadChange.ModuleChanged d.Name
  | DeclKind.StartupCode -> ReloadChange.StartupCodeChanged

/// What removing a declaration must report.
let private expectedRemovalFor (d: SourceDecl) =
  match d.Kind with
  | DeclKind.EntryPointDecl -> ReloadChange.EntryPointChanged
  | DeclKind.StartupCode -> ReloadChange.StartupCodeChanged
  | DeclKind.TypeDecl
  | DeclKind.ValueDecl
  | DeclKind.FunctionDecl
  | DeclKind.NestedModuleDecl -> ReloadChange.DeclarationRemoved d.Name

let private tokens (text: string) = text.Split([| ' '; '\n'; '('; ')' |], System.StringSplitOptions.RemoveEmptyEntries) |> Set.ofArray

[<Tests>]
let planReloadPropertyTests =
  let config = { FsCheckConfig.defaultConfig with maxTest = 300 }
  testList "ReloadPlanning planReload laws" [
    testPropertyWithConfig config
      "WHY — ReloadPlanning.planReload — any declaration set planned against itself patches nothing because a save with no code change must never disturb the app"
    <| Prop.forAll (Arb.fromGen genFileWithRepeats) (fun file ->
      planReload file file = ReloadPlan.PatchFunctions [])

    testPropertyWithConfig config
      "WHY — ReloadPlanning.planReload — trailing whitespace on any declaration patches nothing because an editor's whitespace trim is not a code change"
    <| Prop.forAll (Arb.fromGen genFileWithRepeats) (fun file ->
      let padded =
        { file with Decls = file.Decls |> List.map (fun d -> { d with Text = d.Text.Replace("\n", "  \n") + "   "; Header = d.Header + " " }) }
      planReload file padded = ReloadPlan.PatchFunctions [])

    testPropertyWithConfig config
      "WHY — ReloadPlanning.planReload — body-only function edits patch exactly the edited functions, restarting only for a non-public member they cannot reach, because nothing else changed"
    <| Prop.forAll
         (Arb.fromGen (gen {
            let! file = genUniqueFile
            let! flags = Gen.listOfLength file.Decls.Length (Gen.elements [ true; false ])
            return file, flags }))
         (fun (file, flags) ->
           let editedDecls =
             List.zip file.Decls flags
             |> List.map (fun (d, flag) ->
               match flag && d.Kind = DeclKind.FunctionDecl with
               | true -> edit d
               | false -> d)
           let edited =
             List.zip file.Decls flags
             |> List.filter (fun (d, flag) -> flag && d.Kind = DeclKind.FunctionDecl)
             |> List.map (fst >> edit)
           let editedNames = edited |> List.map _.Name |> Set.ofList
           let hidden =
             file.Decls |> List.filter (fun d -> d.Access <> DeclAccess.Public && not (editedNames.Contains d.Name))
           let unreachable =
             edited
             |> List.choose (fun f ->
               hidden
               |> List.tryFind (fun h -> h.Name <> f.Name && (tokens f.Text).Contains h.Name)
               |> Option.map (fun h -> ReloadChange.UsesNonPublicMember (f.Name, h.Name)))
           match planReload file (fileOf editedDecls), unreachable with
           | ReloadPlan.PatchFunctions patched, [] -> (patched |> List.map _.Name |> Set.ofList) = editedNames
           | ReloadPlan.RestartRequired (first, rest), _ :: _ -> Set.ofList (first :: rest) = Set.ofList unreachable
           | _ -> false)

    testPropertyWithConfig config
      "WHY — ReloadPlanning.planReload — editing one more startup-only declaration keeps every earlier restart reason and adds its own because restart reasons only accumulate"
    <| Prop.forAll
         (Arb.fromGen (gen {
            let! file = genUniqueFile
            // The extra edit targets a declaration that only takes effect at startup.
            let! targetKind = Gen.elements (allKinds |> List.filter (fun k -> k <> DeclKind.FunctionDecl))
            let! target = genDeclNamed "omega" |> Gen.map (fun d -> mkDecl d.Name targetKind d.Access "0")
            let! flags = Gen.listOfLength file.Decls.Length (Gen.elements [ true; false ])
            return fileOf (file.Decls @ [ target ]), flags, target }))
         (fun (file, flags, target) ->
           let before =
             List.zip file.Decls (flags @ [ false ])
             |> List.map (fun (d, flag) ->
               match flag with
               | true -> edit d
               | false -> d)
           let after = before |> List.map (fun d -> match d.Name = target.Name with | true -> edit d | false -> d)
           let reasonsBefore = reasonsOf (planReload file (fileOf before))
           let reasonsAfter = reasonsOf (planReload file (fileOf after))
           Set.isSubset reasonsBefore reasonsAfter && reasonsAfter.Contains (expectedChangeFor target))

    testPropertyWithConfig config
      "WHY — ReloadPlanning.planReload — removing any declaration requires a restart naming it because running code may still use it"
    <| Prop.forAll
         (Arb.fromGen (gen {
            let! file = genUniqueFile
            let! removed = Gen.choose (0, file.Decls.Length - 1)
            return file, removed }))
         (fun (file, removed) ->
           let target = file.Decls.[removed]
           let current = fileOf (file.Decls |> List.indexed |> List.filter (fun (i, _) -> i <> removed) |> List.map snd)
           (reasonsOf (planReload file current)).Contains (expectedRemovalFor target))
  ]
