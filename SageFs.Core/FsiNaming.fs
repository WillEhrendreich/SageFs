module SageFs.FsiNaming

open System
open FSharp.Compiler.Syntax

/// The naming rules F# Interactive uses for the fake namespace path it wraps
/// every submission in — taken from the COMPILER, not guessed from observed
/// strings.
///
/// WHY THIS MODULE EXISTS. SageFs reads FSI's emitted topology back through
/// reflection (hot-reload detour matching, live-test discovery, stack-frame
/// attribution) and therefore has to answer "is this segment FSI's wrapper?"
/// The answer was spelled three different ways in three places, each of them a
/// loose string heuristic:
///
///   * `HotReloadCore.getAllMethods` / `seedPath` — `t.Name.Contains "FSI_"`
///   * `LiveTestingExecutors.normalizeTypeFullName` — `Regex "^FSI_\d+\+"`,
///     which only ever matched the NESTED-TYPE separator and silently left the
///     prefix on a `namespace`-declared type (`FSI_0042.Foo.Bar.Greeting`)
///   * `ErrorMessages.frameworkFrameFragments` — the literal `"fsi_"`
///
/// The compiler has an exact rule, and it is not `Contains`. See
/// `CheckDeclarations.TryStripPrefixPath` below: the segment must START with
/// the prefix and its remainder must be ALL DIGITS. `Contains` additionally
/// strips a user's own `MyFSI_Helpers`; the anchored regex additionally misses
/// the dotted form entirely.
///
/// PROVENANCE. Everything here is either a direct REFERENCE to the compiler's
/// own public constant or a MIRROR of a two-line rule whose upstream text is
/// vendored, with attribution, at
/// `SageFs.Tests/fixtures/fsharp-compiler/fsi-naming-rules.txt` and pinned by
/// `FsiNamingContractTests`. Nothing is bulk-copied.
[<RequireQualifiedAccess>]
module Rules =

  /// REFERENCED, not copied: the compiler's own public constant
  /// (`FSharp.Compiler.Syntax.PrettyNaming.FsiDynamicModulePrefix`, defined at
  /// `src/Compiler/SyntaxTree/PrettyNaming.fs:1097` and exported at
  /// `PrettyNaming.fsi:273`). Because this is the live value out of the
  /// referenced FSharp.Compiler.Service, it cannot drift from the compiler that
  /// actually runs our submissions.
  let dynamicModulePrefix : string = PrettyNaming.FsiDynamicModulePrefix

  /// The separator .NET reflection puts between a type and a NESTED type.
  /// FSI's submission wrapper is a type, so a module nested in a submission
  /// reads back as `FSI_0004+A+B+C` while the compiled twin is `A.B.C`.
  [<Literal>]
  let nestedTypeSeparator = '+'

  /// The separator between namespace/logical-path segments.
  [<Literal>]
  let pathSeparator = '.'

/// Does this ONE path segment name an FSI submission wrapper?
///
/// MIRRORS `CheckDeclarations.TryStripPrefixPath`'s decisive clause
/// (`src/Compiler/Checking/CheckDeclarations.fs:278-279`):
///
///     p.idText.StartsWithOrdinal FsiDynamicModulePrefix &&
///     p.idText[FsiDynamicModulePrefix.Length..] |> String.forall Char.IsDigit
///
/// Mirrored FAITHFULLY, including the edge the compiler accepts: `String.forall`
/// over an empty remainder is `true`, so the bare prefix `"FSI_"` qualifies
/// upstream and qualifies here. Deviating "to be safer" would make this a
/// different rule than the compiler's, which is the whole defect being fixed.
let isDynamicModuleSegment (segment: string) : bool =
  match isNull segment with
  | true -> false
  | false ->
    segment.StartsWith(Rules.dynamicModulePrefix, StringComparison.Ordinal)
    && segment.Substring(Rules.dynamicModulePrefix.Length) |> Seq.forall Char.IsDigit

/// The path segment FSI gives submission `fragmentId`.
///
/// MIRRORS `fsi.fs:2395-2400` (`nextFragmentId` = `$"%04d{fragmentId}"`, wrapped
/// by `mkFragmentPath` as `FsiDynamicModulePrefix + fragmentId ()`) and the same
/// format at `fsi.fs:3215` (`PeekNextFragmentPath`). Note `%04d` is a MINIMUM
/// width, not a fixed one: submission 12345 is `FSI_12345`, which is exactly why
/// `isDynamicModuleSegment` tests "all digits" rather than "four digits".
let fragmentPathSegment (fragmentId: int) : string =
  sprintf "%s%04d" Rules.dynamicModulePrefix fragmentId

/// Split a reflection name into logical path segments. Both separators collapse
/// to the same logical path: `.` separates namespace/module segments and `+`
/// separates nested types, and the compiled twin of an FSI-nested module spells
/// the whole thing with `.`.
let pathSegments (name: string) : string list =
  match String.IsNullOrEmpty name with
  | true -> []
  | false ->
    name.Split([| Rules.pathSeparator; Rules.nestedTypeSeparator |])
    |> Array.toList

/// Strip FSI's submission wrapper from an already-split path.
///
/// MIRRORS the compiler's two strip sites, which agree on the shape:
///   * `CheckDeclarations.fs:736-740` (`IsPartiallyQualifiedNamespace`) drops the
///     HEAD segment when it starts with the prefix, and drops only the head.
///   * `CheckDeclarations.fs:274-281` (`TryStripPrefixPath`) adds the two
///     conditions this function keeps: the remainder must be all digits, and
///     `not (isNil rest)` — the wrapper is never the LAST segment, because a
///     path that is nothing but the wrapper has no logical name to strip to.
let stripDynamicModulePath (segments: string list) : string list =
  match segments with
  | head :: rest when not rest.IsEmpty && isDynamicModuleSegment head -> rest
  | other -> other

/// The logical, compiled-assembly-shaped name for a type FSI emitted:
/// `FSI_0004+A+B+C` and `FSI_0042.Foo.Bar.Greeting` both become the dotted name
/// the project's own build output uses (`A.B.C`, `Foo.Bar.Greeting`).
///
/// A name that carries no wrapper and no nested-type separator — i.e. any name
/// out of a compiled assembly — is returned unchanged, so this is safe to apply
/// to both sides of an identity comparison.
let normalizeReflectionFullName (fullName: string) : string =
  match String.IsNullOrEmpty fullName with
  | true -> ""
  | false ->
    fullName
    |> pathSegments
    |> stripDynamicModulePath
    |> String.concat (string Rules.pathSeparator)

/// Is this dotted/nested name one FSI emitted, rather than one out of a compiled
/// assembly? Used to tell a re-eval'd definition from its compiled twin when the
/// only thing in hand is the name.
///
/// Head-only, like both compiler strip sites: FSI puts its wrapper at the head of
/// the path and nowhere else, so a deeper segment that happens to look like one
/// is a user's own module and must not be treated as FSI's.
let isDynamicName (fullName: string) : bool =
  match pathSegments fullName with
  | head :: _ -> isDynamicModuleSegment head
  | [] -> false

/// Does this free text (a stack frame, a log line, a generated script path)
/// mention an FSI submission wrapper? Covers every decorated spelling reflection
/// and the CLR produce around the same fragment — `FSI_0007`, `$FSI_0007`,
/// `<StartupCode$FSI_0007>`, `FSI_0007.fsx` — because it scans for the prefix
/// rather than for a whole segment.
///
/// FREE-TEXT SPECIALIZATION, stated plainly because it is the one place this
/// module does not mirror upstream exactly: `isDynamicModuleSegment` gets its
/// "all digits" clause for free from the segment boundary, so the compiler
/// accepts a bare `FSI_` with an empty remainder. Free text has no such boundary,
/// so the match has to be anchored positively by AT LEAST ONE digit — otherwise
/// this degrades to the `Contains "fsi_"` it replaces and flags a user's own
/// `MyFSI_Helpers.fs`.
let mentionsDynamicModule (text: string) : bool =
  match String.IsNullOrEmpty text with
  | true -> false
  | false ->
    let prefix = Rules.dynamicModulePrefix
    let rec scan (from: int) =
      match text.IndexOf(prefix, from, StringComparison.Ordinal) with
      | -1 -> false
      | at ->
        let after = at + prefix.Length
        match after < text.Length && Char.IsDigit text.[after] with
        | true -> true
        | false -> scan (at + 1)
    scan 0
