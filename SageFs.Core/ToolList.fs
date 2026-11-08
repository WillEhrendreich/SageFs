namespace SageFs

open SageFs.Affordances

/// Which tools a request is shown. The profile it reads (`ToolListProfile`) is in its own file, ahead of the Settings catalog.
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
