module SageFs.Tests.HolderRewriteTests

/// WHY — `type-migration-direction.md` step 4. A load-time rewrite may only
/// rewrite a binding whose side conditions are all DECIDABLE, and must refuse
/// everything else per binding. A refusal is the common case, so the decision
/// carries every reason rather than a bool that throws them away.
///
/// Each test turns exactly one condition unsafe, so a failure names the
/// condition rather than "the classifier said no".
open Expecto
open Expecto.Flip
open SageFs
open SageFs.HolderRewrite

let private ok (name: string) : BindingFacts =
  { Name = name
    IsModuleLevel = true
    Inline = HolderRewrite.NotInline
    Literal = HolderRewrite.NoLiteralAttr
    AddressTaken = HolderRewrite.NeverAddressTaken
    ByRef = HolderRewrite.NeverByRef
    InQuotation = HolderRewrite.NotInQuotation
    Pinned = HolderRewrite.NotPinned
    Visibility = HolderRewrite.ModulePrivate
    Copy = HolderRewrite.ReferenceSemantics }

let private refused (facts: BindingFacts) =
  match classify facts with
  | Decision.Refuse reasons -> reasons
  | Decision.Rewrite because -> failtestf "expected a refusal, but it was rewritten: %A" because

[<Tests>]
let holderRewriteTests = testList "holder-rewrite side conditions" [

  testCase "WHY — a clean module-level binding IS rewritten" <| fun _ ->
    match classify (ok "tuned") with
    | Decision.Rewrite because ->
      (because |> List.exists (fun r -> r.Contains "module level")) |> Expect.isTrue "it records why"
    | Decision.Refuse why -> failtestf "a clean binding must be rewritten, refused: %A" why

  testCase "WHY — a PUBLIC binding refuses, because an external caller binds the field" <| fun _ ->
    let reasons = refused { (ok "tuned") with Visibility = HolderRewrite.PublicAcrossAssembly }
    (reasons |> List.exists (fun r -> r.Contains "public")) |> Expect.isTrue "it names the reason"

  testCase "WHY — an inline binding refuses, because a cell would be bypassed" <| fun _ ->
    let reasons = refused { (ok "fast") with Inline = HolderRewrite.IsInline }
    (reasons |> List.exists (fun r -> r.Contains "inline")) |> Expect.isTrue "it names the reason"

  testCase "WHY — a [<Literal>] refuses, because the compiler substitutes it" <| fun _ ->
    let reasons = refused { (ok "answer") with Literal = HolderRewrite.HasLiteralAttr }
    (reasons |> List.exists (fun r -> r.Contains "Literal")) |> Expect.isTrue "it names the reason"

  testCase "WHY — an address-taken binding refuses" <| fun _ ->
    let reasons = refused { (ok "slot") with AddressTaken = HolderRewrite.AddressTaken "&slot" }
    (reasons |> List.exists (fun r -> r.Contains "address")) |> Expect.isTrue "it names the reason"

  testCase "WHY — a byref binding refuses, because a holder cannot represent it" <| fun _ ->
    let reasons = refused { (ok "acc") with ByRef = HolderRewrite.PassedByRef "out parameter" }
    (reasons |> List.exists (fun r -> r.Contains "by reference")) |> Expect.isTrue "it names the reason"

  testCase "WHY — a byref-LIKE binding refuses, because the CLR forbids it in a cell" <| fun _ ->
    let reasons = refused { (ok "window") with ByRef = HolderRewrite.ByRefLike "Span<int>" }
    (reasons |> List.exists (fun r -> r.Contains "byref-like")) |> Expect.isTrue "it names the reason"

  testCase "WHY — a binding used inside a quotation refuses, because the rewrite cannot see it" <| fun _ ->
    let reasons = refused { (ok "quoted") with InQuotation = HolderRewrite.InsideQuotation "<@ @>" }
    (reasons |> List.exists (fun r -> r.Contains "quotation")) |> Expect.isTrue "it names the reason"

  testCase "WHY — a storage-pinned binding refuses" <| fun _ ->
    let reasons = refused { (ok "perThread") with Pinned = HolderRewrite.Pinned "[<ThreadStatic>]" }
    (reasons |> List.exists (fun r -> r.Contains "pins")) |> Expect.isTrue "it names the reason"

  testCase "WHY — an unverified struct refuses, because copy semantics are not proven" <| fun _ ->
    let reasons = refused { (ok "point") with Copy = HolderRewrite.StructCopySemanticsUnknown }
    (reasons |> List.exists (fun r -> r.Contains "copy semantics")) |> Expect.isTrue "it names the reason"

  testCase "WHY — a non-module-level binding refuses: there is no single cell to hold it" <| fun _ ->
    let reasons = refused { (ok "local") with IsModuleLevel = false }
    (reasons |> List.exists (fun r -> r.Contains "module-level")) |> Expect.isTrue "it names the reason"

  testCase "WHY — EVERY failing condition is reported, so the message is actionable rather than a symptom" <| fun _ ->
    let reasons =
      refused
        { (ok "bad") with
            Inline = HolderRewrite.IsInline
            Visibility = HolderRewrite.PublicAcrossAssembly }

    (reasons.Length > 1) |> Expect.isTrue "it must not stop at the first problem"

  testCase "WHY — a caller with NO evidence is refused by default, never rewritten" <| fun _ ->
    match unknown "mystery" with
    | Decision.Refuse reasons ->
      (reasons |> List.exists (fun r -> r.Contains "no evidence")) |> Expect.isTrue "it names the default"
    | Decision.Rewrite _ -> failtest "unknown facts must never produce a rewrite"
]
