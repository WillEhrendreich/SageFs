/// ## ReloadPlanning Decision Mutation Tests
///
/// ReloadPlanningTests.fs already characterizes `extractDecls`/`planReload`
/// against a realistic baseline file. This file complements it with the
/// decision-boundary cases that a DU-case swap or dropped guard would flip
/// silently: a brand-new function vs. a brand-new value/type, a removed
/// declaration, a non-public-member patch dependency (the "just-fixed
/// accessibility resolution"), `confirmPatch`'s detoured-vs-not-detoured
/// split, and the `baselineIsTrustworthy` staleness boundary. Each case
/// asserts EXACT equality against the correct value so a mutant that
/// returns any other wrong value is killed too.
module ReloadPlanningDecisionMutationTests

open System
open Expecto
open Expecto.Flip
open SageFs.Features.ReloadPlanning

let private declsOf (source: string) =
  match extractDecls source with
  | Ok decls -> decls
  | Error reason -> failtestf "extractDecls failed: %s" reason

let private mkDecl name kind : SourceDecl =
  { Name = name; Kind = kind; Access = DeclAccess.Public; Header = ""; Text = ""; StartLine = 1; EndLine = 1 }

let reloadPlanningDecisionMutationTests = testList "ReloadPlanning decision mutations" [

  // ── New declarations: only a NEW function is patchable, everything else restarts ──

  testCase "WHY — new_function_in_edited_file_is_Patch_not_Restart — adding a function must not force a restart" <| fun () ->
    let before = declsOf "module M\nlet a () = 1\n"
    let after = declsOf "module M\nlet a () = 1\nlet b () = 2\n"
    match planReload before after with
    | ReloadPlan.PatchFunctions fs -> fs |> List.map (fun d -> d.Name) |> Expect.equal "the new function b must be the only patch" ["b"]
    | ReloadPlan.RestartRequired (first, rest) -> failtestf "adding a function must patch, not restart: %A" (first :: rest)

  // WHY these two say DeclarationAdded rather than ValueChanged/TypeChanged:
  // `outcomeOf`'s None branch used to report an ADDED declaration as *Changed*,
  // which sent the user hunting for an edit they never made — the thing was new,
  // not modified. `DeclarationAdded` was introduced to say what actually
  // happened, and maps to `RestartReason.NewDeclaration` ("did not exist when the
  // app started, so there is nothing running to re-point"). The restart verdict is
  // unchanged and still the point of these cases; only the description is honest now.
  testCase "WHY — new_value_in_edited_file_is_Restart_not_Patch — a new value is built at startup and cannot be patched in" <| fun () ->
    let before = declsOf "module M\nlet a () = 1\n"
    let after = declsOf "module M\nlet a () = 1\nlet v = 42\n"
    match planReload before after with
    | ReloadPlan.RestartRequired (first, rest) ->
      (first :: rest) |> Expect.equal "a brand-new value must restart, described as an ADDED declaration, not a changed one" [ReloadChange.DeclarationAdded "v"]
    | ReloadPlan.PatchFunctions fs -> failtestf "a new value must restart, not patch: %A" (fs |> List.map (fun d -> d.Name))

  testCase "WHY — new_type_in_edited_file_is_Restart — a brand-new type must restart, described as an addition" <| fun () ->
    let before = declsOf "module M\nlet a () = 1\n"
    let after = declsOf "module M\nlet a () = 1\ntype T = { X: int }\n"
    match planReload before after with
    | ReloadPlan.RestartRequired (first, rest) ->
      (first :: rest) |> Expect.equal "a brand-new type must restart, described as an ADDED declaration, not a changed one" [ReloadChange.DeclarationAdded "T"]
    | ReloadPlan.PatchFunctions fs -> failtestf "a new type must restart, not patch: %A" (fs |> List.map (fun d -> d.Name))

  // ── Removed declarations ─────────────────────────────────────────────────

  testCase "WHY — removed_function_is_DeclarationRemoved — deleting a function the app already loaded must force a restart" <| fun () ->
    let before = declsOf "module M\nlet a () = 1\nlet b () = 2\n"
    let after = declsOf "module M\nlet a () = 1\n"
    match planReload before after with
    | ReloadPlan.RestartRequired (first, rest) ->
      (first :: rest) |> Expect.equal "removing b must restart, described as DeclarationRemoved \"b\"" [ReloadChange.DeclarationRemoved "b"]
    | ReloadPlan.PatchFunctions fs -> failtestf "a removed function must restart, not patch: %A" (fs |> List.map (fun d -> d.Name))

  testCase "WHY — removed_entryPoint_is_EntryPointChanged_not_DeclarationRemoved — removal of the special decls keeps their special reason" <| fun () ->
    let before = declsOf "module M\n[<EntryPoint>]\nlet main a = 0\n"
    let after = declsOf "module M\n"
    match planReload before after with
    | ReloadPlan.RestartRequired (first, rest) ->
      (first :: rest) |> Expect.equal "removing the entry point must be reported as EntryPointChanged, not a generic DeclarationRemoved" [ReloadChange.EntryPointChanged]
    | ReloadPlan.PatchFunctions fs -> failtestf "removing the entry point must restart: %A" (fs |> List.map (fun d -> d.Name))

  // ── Signature vs body change on an existing function ────────────────────

  testCase "WHY — function_header_unchanged_body_changed_is_Patch — the roast's own hot path: body edits must be cheap" <| fun () ->
    let before = declsOf "module M\nlet f x = x + 1\n"
    let after = declsOf "module M\nlet f x = x + 2\n"
    match planReload before after with
    | ReloadPlan.PatchFunctions fs -> fs |> List.map (fun d -> d.Name) |> Expect.equal "only f, patched" ["f"]
    | ReloadPlan.RestartRequired (first, rest) -> failtestf "a body-only edit must patch: %A" (first :: rest)

  testCase "WHY — function_header_changed_is_SignatureChanged_restart — a signature change cannot be patched (callers are compiled against the old shape)" <| fun () ->
    let before = declsOf "module M\nlet f (x: int) = x + 1\n"
    let after = declsOf "module M\nlet f (x: int) (y: int) = x + y\n"
    match planReload before after with
    | ReloadPlan.RestartRequired (first, rest) ->
      (first :: rest) |> Expect.equal "a changed function header must restart as SignatureChanged \"f\"" [ReloadChange.SignatureChanged "f"]
    | ReloadPlan.PatchFunctions fs -> failtestf "a signature change must restart, not patch: %A" (fs |> List.map (fun d -> d.Name))

  // ── UsesNonPublicMember: a patch cannot see private/internal members ────

  testCase "WHY — patch_referencing_a_private_function_forces_restart — the patch is compiled OUTSIDE the app assembly and cannot see it" <| fun () ->
    let before = "module M\nlet private helper () = 1\nlet f () = 10\n"
    let after = "module M\nlet private helper () = 1\nlet f () = helper ()\n"
    match planReload (declsOf before) (declsOf after) with
    | ReloadPlan.RestartRequired (first, rest) ->
      (first :: rest) |> Expect.equal "referencing the private helper must restart, naming both the patch and the hidden member"
        [ReloadChange.UsesNonPublicMember ("f", "helper")]
    | ReloadPlan.PatchFunctions fs -> failtestf "referencing a private member must restart, not patch in isolation: %A" (fs |> List.map (fun d -> d.Name))

  testCase "WHY — patch_referencing_a_public_function_is_still_a_patch — public members ARE visible to a patch, so this must not force a restart" <| fun () ->
    let before = "module M\nlet helper () = 1\nlet f () = 10\n"
    let after = "module M\nlet helper () = 1\nlet f () = helper ()\n"
    match planReload (declsOf before) (declsOf after) with
    | ReloadPlan.PatchFunctions fs -> fs |> List.map (fun d -> d.Name) |> Expect.equal "f alone, patched" ["f"]
    | ReloadPlan.RestartRequired (first, rest) -> failtestf "referencing a PUBLIC member must not force a restart: %A" (first :: rest)

  // ── confirmPatch ──────────────────────────────────────────────────────────

  testCase "WHY — confirmPatch_existing_function_detoured_is_Applied" <| fun () ->
    let before = { ModulePath = []; Opens = []; Decls = [ mkDecl "f" DeclKind.FunctionDecl ]; RawSource = None }
    confirmPatch before [ mkDecl "f" DeclKind.FunctionDecl ] [ "M.f" ]
    |> Expect.equal "a function that existed before AND was detoured must be Applied" PatchOutcome.Applied

  testCase "WHY — confirmPatch_existing_function_not_detoured_needs_restart — Harmony silently failing to detour must not be reported as success" <| fun () ->
    let before = { ModulePath = []; Opens = []; Decls = [ mkDecl "f" DeclKind.FunctionDecl ]; RawSource = None }
    confirmPatch before [ mkDecl "f" DeclKind.FunctionDecl ] []
    |> Expect.equal "a function that existed before but was NOT detoured must report RestartNeeded, naming it SignatureChanged"
      (PatchOutcome.RestartNeeded (ReloadChange.SignatureChanged "f", []))

  testCase "WHY — confirmPatch_brand_new_function_is_Applied_without_requiring_detour — a function that never existed has nothing to detour onto" <| fun () ->
    let before = { ModulePath = []; Opens = []; Decls = []; RawSource = None }
    confirmPatch before [ mkDecl "brandNew" DeclKind.FunctionDecl ] []
    |> Expect.equal "a brand-new patched function must be Applied even with an empty detour list (it didn't need detouring)" PatchOutcome.Applied

  testCase "WHY — confirmPatch_matches_detour_by_exact_name_or_dotted_suffix" <| fun () ->
    let before = { ModulePath = []; Opens = []; Decls = [ mkDecl "f" DeclKind.FunctionDecl ]; RawSource = None }
    confirmPatch before [ mkDecl "f" DeclKind.FunctionDecl ] [ "Some.Nested.Module.f" ]
    |> Expect.equal "a dotted detour name ending in \".f\" must count as detouring f" PatchOutcome.Applied

  // ── baselineIsTrustworthy: `<=` not `<` ──────────────────────────────────

  testCase "WHY — baselineIsTrustworthy_source_exactly_at_assembly_time_is_trustworthy" <| fun () ->
    let t = DateTime(2026, 1, 1, 12, 0, 0)
    baselineIsTrustworthy t t
    |> Expect.isTrue "a source write time EXACTLY equal to the assembly write time must be trustworthy (`<=`, not `<`)"

  testCase "WHY — baselineIsTrustworthy_source_one_tick_after_assembly_is_not_trustworthy" <| fun () ->
    let t = DateTime(2026, 1, 1, 12, 0, 0)
    baselineIsTrustworthy t (t.AddTicks 1L)
    |> Expect.isFalse "a source write time one tick AFTER the assembly build must be untrustworthy — the running app never saw this edit"

  testCase "WHY — baselineIsTrustworthy_source_before_assembly_is_trustworthy" <| fun () ->
    let assemblyTime = DateTime(2026, 1, 1, 12, 0, 0)
    baselineIsTrustworthy assemblyTime (assemblyTime.AddSeconds -1.0)
    |> Expect.isTrue "a source written before the assembly build must be trustworthy"
]
