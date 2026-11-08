namespace SageFs

open SageFs.Affordances

/// What `tools/list` shows a request that presented no credential.
///
/// The MCP revision of 2026-07-28 says a tool list MUST NOT vary per connection or as a side effect of another
/// request, and MAY vary by the authorization presented. So the profile is a setting of the daemon, read once at start,
/// and it applies to the one caller whose list was never cut by anything: the caller that presents nothing. A member
/// token keeps its role's list and the operator credential keeps the full list, whatever the profile says.
[<RequireQualifiedAccess>]
type ToolListProfile =
  /// Every registered tool, as the list has always been.
  | Full
  /// The tools a first session needs, and `discover_features`, which names every tool left out. A tool outside the list
  /// is still admitted when a call names it: the list is what a client is shown, not what it may call.
  | Starter

module ToolListProfile =
  let all : ToolListProfile list = [ ToolListProfile.Full; ToolListProfile.Starter ]

  let toToken =
    function
    | ToolListProfile.Full -> "Full"
    | ToolListProfile.Starter -> "Starter"

  let tryParse (raw: string) : Result<ToolListProfile, Capability.UnknownName> =
    let wanted = if isNull raw then "" else raw.Trim()
    match all |> List.tryFind (fun p -> System.String.Equals(toToken p, wanted, System.StringComparison.OrdinalIgnoreCase)) with
    | Some profile -> Ok profile
    | None -> Error { Given = wanted; Expected = all |> List.map toToken }

  /// Nothing existing changes: the list stays whole until a person sets the profile. The default flips only after the
  /// measured pass rate on a fixed task set shows no loss with the starter list (docs/plans/small-default-tool-list.md).
  let defaultProfile : ToolListProfile = ToolListProfile.Full

  /// The environment variable that sets the daemon's profile.
  let envVar = "SAGEFS_TOOL_LIST"

  /// The profile for `getEnv`. Unset is the default. Set but unreadable is the default too, because the safe way to
  /// miss is to hide nothing: a typo must not take tools out of a client's list.
  let fromEnvironment (getEnv: string -> string) : ToolListProfile =
    match getEnv envVar with
    | null -> defaultProfile
    | value when System.String.IsNullOrWhiteSpace value -> defaultProfile
    | value ->
      match tryParse value with
      | Ok profile -> profile
      | Error _ -> defaultProfile

module ToolList =
  /// The tools a first session needs: make a session, list sessions, read a session's state, run code, check code without
  /// running it, run tests, cancel a runaway eval, reset, and the doorway to every other tool. Each is a case of the
  /// `ToolName` union, so a renamed tool is a compile error here and not a silent gap. A project can be found with the
  /// shell, and `stop_session` and `get_available_projects` are callable by name and named by `discover_features`.
  let starterTools : ToolName list =
    [ ToolName.CreateProjectSession
      ToolName.GetSessionStatus
      ToolName.ListSessions
      ToolName.SendFsharpCode
      ToolName.CheckFsharpCode
      ToolName.RunTests
      ToolName.CancelEval
      ToolName.HardResetFsiSession
      ToolName.DiscoverFeatures ]

  let starterNames : Set<string> = starterTools |> List.map ToolName.toToolName |> Set.ofList

  /// The tools a request is shown. A caller that presented nothing, on a daemon whose policy lets it act, sees the
  /// profile's tools. Every other caller sees what `Capability.listedTools` says, so the profile cannot widen a list
  /// that a token or `TokenRequired` cut. Registered order is kept.
  let listed
    (profile: ToolListProfile)
    (policy: Capability.IdentityPolicy)
    (listedFor: Capability.ListedFor)
    (registered: string list)
    : string list =
    match listedFor with
    | Capability.ListedFor.Operator
    | Capability.ListedFor.Member _ -> Capability.listedTools policy listedFor registered
    | Capability.ListedFor.NoCredential ->
      match policy with
      | Capability.IdentityPolicy.TokenRequired -> Capability.listedTools policy listedFor registered
      | Capability.IdentityPolicy.ConnectionsAllowed ->
        match profile with
        | ToolListProfile.Full -> Capability.listedTools policy listedFor registered
        | ToolListProfile.Starter -> registered |> List.filter (fun tool -> Set.contains tool starterNames)
