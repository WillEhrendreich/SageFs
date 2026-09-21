module SageFs.Tests.FsiNamingContractTests

/// Pins `SageFs.Core/FsiNaming.fs` against the F# compiler's own rules.
///
/// Two independent pins, because they fail for different reasons:
///
///   1. THE LIVE PIN. `FsiNaming.Rules.dynamicModulePrefix` is the constant out
///      of the referenced FSharp.Compiler.Service, so it cannot drift from the
///      compiler that actually runs our submissions — but the VENDORED excerpt
///      could go stale and start describing a different compiler. Asserting the
///      live value equals the quoted literal catches that.
///   2. THE MIRROR PIN. The predicate and the fragment format are MIRRORED (two
///      lines each), not referenced, so nothing but a test stops them being
///      "improved" into something the compiler does not do. Each assertion below
///      names the upstream clause it encodes; the excerpt text is read out of the
///      fixture so the rule and its justification cannot be edited apart.
///
/// The fixture carries upstream's MIT notice and the commit it was taken from.
/// If it is missing or its attribution is gone, these tests FAIL — attribution is
/// a gate, not a comment.

open Expecto
open Expecto.Flip
open System.IO
open SageFs.FsiNaming

let private fixturePath =
  Path.Combine(__SOURCE_DIRECTORY__, "fixtures", "fsharp-compiler", "fsi-naming-rules.txt")

let private fixtureText = lazy (File.ReadAllText fixturePath)

/// The body of one `--- BEGIN EXCERPT <name> ---` / `--- END EXCERPT <name> ---`
/// block. Fails loudly when the block is absent: a silently-empty excerpt would
/// turn every assertion below into a tautology.
let private excerpt (name: string) : string =
  let text = fixtureText.Value
  let opening = sprintf "--- BEGIN EXCERPT %s ---" name
  let closing = sprintf "--- END EXCERPT %s ---" name
  match text.IndexOf opening, text.IndexOf closing with
  | -1, _ -> failtestf "fixture %s has no '%s' block" fixturePath opening
  | _, -1 -> failtestf "fixture %s has no '%s' block" fixturePath closing
  | start, finish when finish <= start ->
    failtestf "fixture %s has '%s' before '%s'" fixturePath closing opening
  | start, finish -> text.Substring(start + opening.Length, finish - start - opening.Length)

[<Tests>]
let fsiNamingContractTests =
  testList "FsiNaming — contract with the F# compiler source" [

    testCase "WHY — the vendored excerpts carry upstream's MIT notice and the commit they came from, so the mirror is attributable" <| fun _ ->
      File.Exists fixturePath
      |> Expect.isTrue (sprintf "%s must exist — it is the reviewable provenance for every mirrored rule below" fixturePath)
      let text = fixtureText.Value
      text.Contains "LICENSE:  MIT"
      |> Expect.isTrue "the fixture must state upstream's MIT license"
      text.Contains "Copyright (c) Microsoft Corporation."
      |> Expect.isTrue "the fixture must carry upstream's copyright line"
      text.Contains "UPSTREAM: https://github.com/dotnet/fsharp"
      |> Expect.isTrue "the fixture must name the upstream repository"
      text.Contains "COMMIT:   cdb9dc5e5"
      |> Expect.isTrue
        "the fixture must pin the commit the excerpts were taken from — a refreshed excerpt without a refreshed commit is an unverifiable quote"

    // ── 1. THE LIVE PIN ──────────────────────────────────────────────────
    testCase "WHY — the prefix is REFERENCED out of FSharp.Compiler.Service and still equals the vendored literal" <| fun _ ->
      // If these ever disagree, the excerpt is describing a different compiler
      // than the one SageFs.Core links, and every rule mirrored from it is suspect.
      (excerpt "prefix-constant").Contains "let FsiDynamicModulePrefix = \"FSI_\""
      |> Expect.isTrue "the vendored PrettyNaming.fs excerpt must still show the literal this test compares against"
      Rules.dynamicModulePrefix
      |> Expect.equal
        "the live FSharp.Compiler.Service constant must equal the vendored literal"
        "FSI_"

    testCase "WHY — the prefix is public API, so referencing it never needs an internals hack" <| fun _ ->
      (excerpt "prefix-constant-is-public").Contains "val FsiDynamicModulePrefix: string"
      |> Expect.isTrue
        "PrettyNaming.fsi must still export the constant WITHOUT `internal` — the moment it goes internal, FsiNaming has to vendor the literal and say so"

    // ── 2. THE MIRROR PIN: the segment predicate ─────────────────────────
    testCase "WHY — isDynamicModuleSegment encodes StartsWithOrdinal + all-digits, not Contains" <| fun _ ->
      let upstream = excerpt "segment-predicate"
      upstream.Contains "p.idText.StartsWithOrdinal FsiDynamicModulePrefix"
      |> Expect.isTrue "upstream must still anchor the match at the START of the segment"
      upstream.Contains "String.forall Char.IsDigit"
      |> Expect.isTrue "upstream must still require the remainder to be all digits"

      // The clause "StartsWithOrdinal": anchored, so a segment that merely
      // CONTAINS the prefix is not FSI's. This is the exact difference from the
      // `t.Name.Contains \"FSI_\"` heuristic it replaces.
      isDynamicModuleSegment "FSI_0004"
      |> Expect.isTrue "a real submission wrapper"
      isDynamicModuleSegment "MyFSI_0004"
      |> Expect.isFalse "a user's own type that merely CONTAINS the prefix is not FSI's — `Contains` got this wrong"

      // The clause "String.forall Char.IsDigit": the remainder is digits only.
      isDynamicModuleSegment "FSI_Helpers"
      |> Expect.isFalse "a user module literally prefixed FSI_ but not numbered is not a submission wrapper"
      isDynamicModuleSegment "FSI_0004a"
      |> Expect.isFalse "one non-digit in the remainder disqualifies the segment"

      // `%04d` is a MINIMUM width (see the fragment-format excerpt), so the
      // predicate must not be narrowed to exactly four digits.
      isDynamicModuleSegment "FSI_12345"
      |> Expect.isTrue "submission 12345 overflows the %04d pad — 'all digits' must not become 'four digits'"

      // Mirrored faithfully INCLUDING the edge upstream accepts: `String.forall`
      // over an empty remainder is true. Deviating here would make this a
      // different rule than the compiler's.
      isDynamicModuleSegment "FSI_"
      |> Expect.isTrue "upstream's String.forall over an empty remainder is true — the mirror matches rather than 'improves'"

      isDynamicModuleSegment "Greeting" |> Expect.isFalse "an ordinary compiled type name"
      isDynamicModuleSegment null |> Expect.isFalse "a null segment is not FSI's and must not throw"

    testCase "WHY — the wrapper is stripped from the HEAD only, and never when it is the whole path" <| fun _ ->
      let headOnly = excerpt "head-only-strip"
      headOnly.Contains "| (h, _) :: t -> if h.StartsWithOrdinal FsiDynamicModulePrefix then t else p"
      |> Expect.isTrue "upstream must still strip only the head segment"
      (excerpt "segment-predicate").Contains "not (isNil rest)"
      |> Expect.isTrue "upstream must still refuse to strip when nothing would be left"

      stripDynamicModulePath [ "FSI_0004"; "A"; "B" ]
      |> Expect.equal "the head wrapper goes" [ "A"; "B" ]
      stripDynamicModulePath [ "A"; "FSI_0004"; "B" ]
      |> Expect.equal "a wrapper-shaped segment that is NOT the head is the user's own and stays" [ "A"; "FSI_0004"; "B" ]
      stripDynamicModulePath [ "FSI_0004" ]
      |> Expect.equal "`not (isNil rest)` — stripping the only segment would leave no name at all" [ "FSI_0004" ]
      stripDynamicModulePath []
      |> Expect.equal "an empty path is returned unchanged" []

    // ── 2. THE MIRROR PIN: the fragment format ───────────────────────────
    testCase "WHY — fragmentPathSegment reproduces FSI's %04d fragment id" <| fun _ ->
      let upstream = excerpt "fragment-format"
      upstream.Contains "$\"%04d{fragmentId}\""
      |> Expect.isTrue "upstream's nextFragmentId must still be %04d"
      upstream.Contains "FsiDynamicModulePrefix + fragmentId ()"
      |> Expect.isTrue "upstream must still build the segment as prefix + id"
      (excerpt "fragment-format-peek").Contains "FsiDynamicModulePrefix + $\"%04d{fragmentId + 1}\""
      |> Expect.isTrue "PeekNextFragmentPath must still agree with nextFragmentId on the format"

      fragmentPathSegment 1 |> Expect.equal "the first submission" "FSI_0001"
      fragmentPathSegment 42 |> Expect.equal "zero-padded to four" "FSI_0042"
      fragmentPathSegment 12345 |> Expect.equal "%04d is a minimum width, not a truncation" "FSI_12345"

      // The generated segment must satisfy the predicate that recognizes it —
      // the two mirrored rules describe the same thing and must agree.
      [ 1; 42; 9999; 12345 ]
      |> List.map (fragmentPathSegment >> isDynamicModuleSegment)
      |> Expect.equal
        "every id FSI can generate must be recognized by the predicate the two rules share"
        [ true; true; true; true ]
  ]

[<Tests>]
let fsiNamingBehaviourTests =
  testList "FsiNaming — normalization across FSI's two spellings" [

    testCase "WHY — a module-declared file comes back with '+' separators and normalizes to the compiled name" <| fun _ ->
      // `module A.B` evaluated in FSI: the submission wrapper is a TYPE, so the
      // modules nested under it read back with the nested-type separator.
      normalizeReflectionFullName "FSI_0005+A+B+C"
      |> Expect.equal "the compiled twin is A.B.C" "A.B.C"

    testCase "WHY — a namespace-declared file comes back DOTTED, the case the old regex silently missed" <| fun _ ->
      // THE BUG. `Regex.Replace(n, \"^FSI_\\d+\\+\", \"\")` required the wrapper to
      // be followed by '+'. FSI represents a `namespace Foo.Bar` file's types as
      // top-level types whose Namespace is `FSI_0042.Foo.Bar` — dotted — so the
      // regex matched nothing, the wrapper survived, and the re-eval'd test got a
      // different TestId from its compiled twin.
      normalizeReflectionFullName "FSI_0042.Foo.Bar.Greeting"
      |> Expect.equal "the compiled twin is Foo.Bar.Greeting" "Foo.Bar.Greeting"

    testCase "WHY — the two spellings of one logical type normalize to the SAME name, which is what makes them merge" <| fun _ ->
      normalizeReflectionFullName "FSI_0042.Foo.Bar.Greeting"
      |> Expect.equal
        "a namespace-declared type and its nested-spelled twin must be one identity"
        (normalizeReflectionFullName "FSI_0042+Foo+Bar+Greeting")

    testCase "WHY — a compiled name is returned unchanged, so normalizing both sides is safe" <| fun _ ->
      normalizeReflectionFullName "Foo.Bar.Greeting"
      |> Expect.equal "no wrapper, no nesting, no change" "Foo.Bar.Greeting"
      normalizeReflectionFullName "MyFSI_Helpers.Thing"
      |> Expect.equal "a user type that merely contains the prefix is untouched" "MyFSI_Helpers.Thing"
      normalizeReflectionFullName null |> Expect.equal "null normalizes to empty, never throws" ""
      normalizeReflectionFullName "" |> Expect.equal "empty stays empty" ""

    testCase "WHY — a nested COMPILED type still flattens to its dotted name" <| fun _ ->
      // Reflection spells a nested type `Outer+Inner` even in a compiled
      // assembly; the live-test discovery merge keys on the dotted form.
      normalizeReflectionFullName "Foo.Bar+Greeting"
      |> Expect.equal "the nested-type separator always becomes a path separator" "Foo.Bar.Greeting"

    testCase "WHY — isDynamicName answers off the head segment, like both compiler strip sites" <| fun _ ->
      isDynamicName "FSI_0007+A+B" |> Expect.isTrue "an FSI-emitted name"
      isDynamicName "A.B.Greeting" |> Expect.isFalse "a compiled name"
      isDynamicName "A.FSI_0007.B"
      |> Expect.isFalse "a wrapper-shaped segment that is not the head belongs to the user, not to FSI"
      isDynamicName "" |> Expect.isFalse "empty is not dynamic"

    testCase "WHY — mentionsDynamicModule replaces Contains \"fsi_\" and stops classifying a user's own file as framework noise" <| fun _ ->
      // Every decorated spelling the CLR puts around one fragment.
      mentionsDynamicModule "   at <StartupCode$FSI_0007>.$FSI_0007.main@() in FSI_0007.fsx:line 3"
      |> Expect.isTrue "the startup-code frame FSI actually emits"
      mentionsDynamicModule "   at FSI_0003.Program.foo()"
      |> Expect.isTrue "a plain fragment frame"
      // The regression the old literal caused: a user file whose name happens to
      // contain the four characters was classified as framework noise, so the
      // summary reported no user frame at all.
      mentionsDynamicModule "   at Acme.Run() in /src/MyFSI_Helpers.fs:line 12"
      |> Expect.isFalse "a user's own MyFSI_Helpers.fs is NOT an FSI wrapper — `Contains \"fsi_\"` said it was"
      mentionsDynamicModule "   at Acme.Run() in /src/Program.fs:line 12"
      |> Expect.isFalse "an ordinary user frame"
      mentionsDynamicModule "" |> Expect.isFalse "empty text mentions nothing"
      mentionsDynamicModule null |> Expect.isFalse "null text mentions nothing and must not throw"
  ]
