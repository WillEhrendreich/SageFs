/// Pure decision for whether SageFs can host a project, based on the target
/// framework(s) declared in its `.fsproj`. No IO in the classifier itself —
/// `classify` takes raw XML text; `classifyProjectFile` is the one IO edge,
/// for the single caller (SessionManager's `CreateSession` handling) that
/// needs to read a real file off disk.
///
/// Why this exists: SageFs's FSI host runs on modern .NET (Core); it cannot
/// load .NET Framework assemblies at all. Before this module there was no
/// check anywhere for a project's target framework (verified: `grep
/// TargetFramework` across RuntimeCompat.fs, RuntimeSelection.fs and
/// ProjectLoading.fs returned nothing). A .NET Framework project has no
/// `runtimeconfig.json` (a .NET-Core-era file), so warmup fell through to
/// `RuntimeCompat`'s build-detection errors and told the user "has not been
/// built (no bin/ directory)" or "runtimeconfig.json names no framework
/// version" — both false and actively misleading: the project builds fine,
/// it just produces output the FSI host cannot load. This module gives the
/// daemon a TRUE reason instead, so SessionManager's CreateSession handling
/// (the single owner of session creation) can refuse the session up front —
/// see SageFsError.ProjectFrameworkNotHostable.
module SageFs.ProjectCompatibility

open System
open System.IO
open System.Text.RegularExpressions
open System.Xml.Linq

/// Why a single target framework moniker cannot be hosted.
[<RequireQualifiedAccess>]
type UnsupportedTfmReason =
  /// A .NET Framework TFM (the dot-less short form: net11..net481) — the
  /// FSI host runs on modern .NET (Core) and cannot load .NET Framework
  /// assemblies at all. There is no roll-forward or compatibility shim for
  /// this the way there is for a merely-newer .NET (Core) version
  /// (see RuntimeCompat).
  | NetFramework

let describeUnsupportedReason =
  function
  | UnsupportedTfmReason.NetFramework ->
    "SageFs's FSI host runs on modern .NET (Core), which cannot load .NET Framework assemblies"

/// Where a user can follow — and push on — support for a framework SageFs
/// cannot host yet. Per-reason, so a second `UnsupportedTfmReason` cannot
/// quietly inherit a pointer that says nothing about it.
let trackingIssue =
  function
  | UnsupportedTfmReason.NetFramework -> "https://github.com/WillEhrendreich/SageFs/issues/135"

/// The verdict for ONE target framework moniker string.
[<RequireQualifiedAccess>]
type TfmVerdict =
  /// A .NET (Core) TFM at net5.0 or newer — the FSI host can load it.
  /// `major` is captured here so a multi-targeted project's "which one wins"
  /// choice never has to re-parse the moniker string.
  | Supported of tfm: string * major: int
  /// A TFM the FSI host is known not to be able to load, with why.
  | Unsupported of tfm: string * reason: UnsupportedTfmReason
  /// Not confidently classifiable either way — netstandard (a library-only
  /// moniker with no runtime of its own — see the scope note on
  /// netstandard below), an unrecognised/exotic moniker, or anything else
  /// this classifier does not have a confident rule for. Falls through to
  /// today's behavior; NEVER treated as a refusal.
  | Unknown of tfm: string * reason: string

/// Why a project's target framework(s) could not be read at all.
[<RequireQualifiedAccess>]
type ProjectReadError =
  | MalformedXml of detail: string
  | NoTargetFrameworkElement
  | Empty

let private describeReadError =
  function
  | ProjectReadError.MalformedXml detail -> sprintf "the project file is not valid XML (%s)" detail
  | ProjectReadError.NoTargetFrameworkElement -> "the project file names no <TargetFramework> or <TargetFrameworks>"
  | ProjectReadError.Empty -> "the project file is empty"

/// The verdict for a WHOLE project (its `.fsproj`), after accounting for
/// multi-targeting: hostable if ANY declared TFM is Supported.
[<RequireQualifiedAccess>]
type ProjectHostability =
  /// SageFs can host this project. `chosen` is the TFM it will use — the
  /// only one when single-targeted, or the highest-major Supported one when
  /// multi-targeted.
  | Hostable of chosen: string
  /// Every declared TFM is confidently unhostable — safe to refuse.
  | NotHostable of targetFrameworks: string list * reason: UnsupportedTfmReason
  /// Could not determine with confidence: an unreadable/malformed project, a
  /// project with no TFM element, or a TFM set with no Supported member but
  /// at least one Unknown one. MUST fall through to today's behavior —
  /// never a refusal. Be conservative: an unreadable or unusual project is
  /// Indeterminate, not NotHostable.
  | Indeterminate of reason: string

// .NET Framework's SDK-style short TFM has no dot: net11, net20, net35,
// net40, net403, net45, net451, net452, net46, net461, net462, net47,
// net471, net472, net48, net481. Modern .NET (Core) TFMs always carry a
// dot: net5.0, net6.0, ... net10.0, net11.0. That dot is the reliable
// discriminator — SDK-style projects never write a dotted .NET Framework
// TFM or a dot-less modern one.
let private netFrameworkShort = Regex(@"^net\d{2,3}$", RegexOptions.Compiled)
let private netCoreDotted = Regex(@"^net(\d+)\.\d+$", RegexOptions.Compiled)

/// Classify one TFM moniker, e.g. "net8.0", "net48", "netstandard2.0".
///
/// Scope note on netstandard: a netstandard library is normal and common as
/// a project REFERENCE (a netstandard2.0 library referenced by a net10 app
/// builds and runs fine) — this classifier only ever looks at the TFM of a
/// session's own top-level project(s), never at transitive references, so
/// that everyday case never reaches this function. For a netstandard
/// project named DIRECTLY as a session's own project, hostability actually
/// depends on what loads it, which this classifier cannot see — so it is
/// deliberately Unknown, never Unsupported, per the "don't block what
/// you're not certain about" rule.
let classifyTfm (rawTfm: string) : TfmVerdict =
  let tfm = rawTfm.Trim()
  match tfm with
  | t when t.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase) ->
    TfmVerdict.Unknown(
      t,
      "netstandard is a library-only moniker with no runtime of its own — hostability depends on what actually loads it, which this classifier cannot see")
  | t when netFrameworkShort.IsMatch t -> TfmVerdict.Unsupported(t, UnsupportedTfmReason.NetFramework)
  | t ->
    let m = netCoreDotted.Match t
    match m.Success with
    | true -> TfmVerdict.Supported(t, int m.Groups.[1].Value)
    | false -> TfmVerdict.Unknown(t, sprintf "'%s' is not a recognised target framework moniker" t)

let private xname (local: string) = XName.Get(local)

/// Read the `<TargetFramework>`/`<TargetFrameworks>` element text from raw
/// `.fsproj` XML. `<TargetFrameworks>` (plural) is semicolon-split for
/// multi-targeting. A project with neither element is
/// `NoTargetFrameworkElement` — every SDK-style project declares one, so its
/// absence means this classifier cannot say anything, not that the project
/// is exotic.
let readTargetFrameworks (fsprojXml: string) : Result<string list, ProjectReadError> =
  match String.IsNullOrWhiteSpace fsprojXml with
  | true -> Error ProjectReadError.Empty
  | false ->
    try
      let doc = XDocument.Parse fsprojXml
      let elementText name =
        doc.Descendants(xname name)
        |> Seq.tryHead
        |> Option.map (fun e -> e.Value.Trim())
        |> Option.filter (fun s -> s <> "")
      match elementText "TargetFramework", elementText "TargetFrameworks" with
      | Some single, _ -> Ok [ single ]
      | None, Some multi ->
        Ok(
          multi.Split(';')
          |> Array.map (fun s -> s.Trim())
          |> Array.filter (fun s -> s <> "")
          |> Array.toList
        )
      | None, None -> Error ProjectReadError.NoTargetFrameworkElement
    with ex ->
      Error(ProjectReadError.MalformedXml ex.Message)

/// Classify a whole project from its raw `.fsproj` XML text. Pure — no IO.
let classify (fsprojXml: string) : ProjectHostability =
  match readTargetFrameworks fsprojXml with
  | Error err -> ProjectHostability.Indeterminate(describeReadError err)
  | Ok tfms ->
    let verdicts = tfms |> List.map classifyTfm
    let supported =
      verdicts
      |> List.choose (function
        | TfmVerdict.Supported(t, major) -> Some(t, major)
        | _ -> None)
    match supported with
    | _ :: _ ->
      let chosen = supported |> List.maxBy snd |> fst
      ProjectHostability.Hostable chosen
    | [] ->
      let anyUnknown =
        verdicts
        |> List.exists (function
          | TfmVerdict.Unknown _ -> true
          | _ -> false)
      match anyUnknown with
      | true ->
        ProjectHostability.Indeterminate(
          sprintf "could not confidently classify target framework(s) %s" (tfms |> String.concat "; ")
        )
      | false ->
        // Every declared TFM classified confidently Unsupported.
        let reason =
          verdicts
          |> List.tryPick (function
            | TfmVerdict.Unsupported(_, r) -> Some r
            | _ -> None)
          |> Option.defaultValue UnsupportedTfmReason.NetFramework
        ProjectHostability.NotHostable(tfms, reason)

/// Read a `.fsproj` off disk and classify it. The one IO edge in this
/// module — any failure to read the file (missing, permission denied, not
/// actually readable text) is `Indeterminate`, never a refusal: a project
/// SessionManager cannot even read is not one this classifier can be
/// confident about.
let classifyProjectFile (path: string) : ProjectHostability =
  try
    classify (File.ReadAllText path)
  with ex ->
    ProjectHostability.Indeterminate(sprintf "could not read '%s': %s" path ex.Message)

/// The exact, actionable message for a project SageFs cannot host today.
/// Names the project, the target framework(s) found, why, and where the
/// support is tracked. It never blames the user's build, and it is careful
/// to say "yet": this is the state of the current host, not a decision that
/// SageFs will never target the framework in question.
let describeUnhostable (project: string) (targetFrameworks: string list) (reason: UnsupportedTfmReason) : string =
  sprintf
    "%s targets %s. %s, so SageFs cannot host this project yet — support is tracked at %s."
    project
    (targetFrameworks |> String.concat ", ")
    (describeUnsupportedReason reason)
    (trackingIssue reason)

// ─── Toolchain fit (Fable / browser projects) ───────────────

/// What a project's TOOLCHAIN means for what SageFs can do with it — a
/// separate axis from `ProjectHostability`, which is about the target
/// framework. A Fable client project is perfectly hostable by the target-
/// framework rule (it targets net8.0/net10.0 like anything else) and it
/// genuinely builds, so it must never be refused. It just cannot do the one
/// thing its author came for.
///
/// Measured, not assumed. A Fable client project:
///   * restores and `dotnet build`s with zero warnings — every Fable package
///     ships a real `lib/netstandard2.0/*.dll`, none injects MSBuild targets;
///   * does NOT poison a solution — a Server/Shared/Client SAFE-shaped
///     solution builds clean, and the Client produces a real `Client.dll`;
///   * only fails when its browser bindings are EVALUATED, with three
///     different messages depending on the package.
///
/// So there is nothing to detect at build time and nothing to refuse. The
/// honest thing SageFs can do is say, at session creation, which half of the
/// project it can actually help with.
[<RequireQualifiedAccess>]
type ToolchainFit =
  /// An ordinary .NET project: everything SageFs does applies.
  | DotNet
  /// A Fable client project — F# compiled to JavaScript, whose real runtime is
  /// a browser. `markers` are the references that said so.
  | FableClient of markers: string list

/// Classify a project's toolchain from its package references. Anything with
/// no browser-only Fable reference is `DotNet` — the conservative default, and
/// the same "don't claim what you can't see" rule the TFM classifier follows.
let classifyToolchain (packageRefs: string list) : ToolchainFit =
  match WorkflowTypes.FableMarkers.findMatches packageRefs with
  | [] -> ToolchainFit.DotNet
  | markers -> ToolchainFit.FableClient markers

/// The truthful answer for a Fable client project: what SageFs does for it,
/// what it does not, and WHY — stated as a different machine rather than a
/// missing feature, because that is what it is.
///
/// Deliberately not a refusal and deliberately not silent. The project works;
/// the user just needs to know which parts of it SageFs is the right tool for.
let describeFableClient (project: string) (markers: string list) : string =
  sprintf
    "%s references %s, so it is a Fable client project — its real runtime is JavaScript in a browser. \
     It builds and loads here, and its plain .NET code (domain types, MVU update functions, anything not \
     touching the browser) evaluates and hot-reloads normally. What will NOT work is evaluating the browser \
     bindings: Fable.Core's JS/JsInterop throw \"You've hit dummy code used for Fable bindings\", Browser.Dom \
     throws \"JS only\", and Feliz view builders throw InvalidCastException. That is not a missing feature — \
     SageFs hot reload patches .NET method bodies in a live CLR process, and there is no CLR method behind a \
     Fable view. → Point SageFs at your server and shared projects for the full REPL, hot reload and live \
     testing, and let Vite's HMR handle the client."
    project
    (markers |> String.concat ", ")

/// Scan a session-create request's projects for the first one SageFs is
/// CONFIDENTLY unable to host. `None` means proceed — every project is
/// either Hostable or Indeterminate (the conservative default when in
/// doubt), matching today's behavior for anything this classifier can't
/// read or doesn't recognise.
let findUnhostable (projects: string list) : (string * string list * UnsupportedTfmReason) option =
  projects
  |> List.tryPick (fun project ->
    match classifyProjectFile project with
    | ProjectHostability.NotHostable(tfms, reason) -> Some(project, tfms, reason)
    | ProjectHostability.Hostable _
    | ProjectHostability.Indeterminate _ -> None)

/// One advisory line per project whose TOOLCHAIN means SageFs can only help
/// with part of it — today, a Fable client, whose real runtime is a browser.
///
/// Advisory, never a refusal: a Fable client project genuinely builds and
/// loads, and a Fable project sitting in a solution does not break a session
/// on the rest of it. The user is simply told which half of their project
/// SageFs is the right tool for. Pure — callers supply the references.
///
/// Lives here rather than in Mcp.fs because it is presentation over THIS
/// module's own types and needs nothing from the MCP surface — and because
/// Mcp.fs is the accretion hub the file-size ratchet exists to shrink.
let formatToolchainAdvisories (projectRefs: (string * string list) list) : string list =
  projectRefs
  |> List.choose (fun (project, refs) ->
    match classifyToolchain refs with
    | ToolchainFit.DotNet -> None
    | ToolchainFit.FableClient markers ->
      Some(sprintf "ℹ️ %s" (describeFableClient (Path.GetFileName project) markers)))
