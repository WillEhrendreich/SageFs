namespace SageFs

open System
open System.IO

/// What a directory's `.SageFs/config.fsx` asks of the session that starts in it, checked and ready to hand to the
/// FSI host: the garbage collector it runs under, extra compiler arguments and environment variables. Pure. The edge (`IsolatedFsiSession.start`) reads the result.
module SessionOptions =

  /// Why one entry of the config was not honored. The rest of the config still is.
  type Refusal =
    | EnvNameEmpty
    | EnvNameInvalid of name: string
    | EnvNamePoisoned of name: string

  let describe (refusal: Refusal) : string =
    match refusal with
    | EnvNameEmpty ->
      "A session environment entry has an empty name, so it was dropped. → Give the variable a name in SessionEnv."
    | EnvNameInvalid name ->
      sprintf "'%s' cannot be an environment variable name (it holds '=' or a NUL), so it was dropped. → Rename it in SessionEnv." name
    | EnvNamePoisoned name ->
      sprintf "%s is one of the variables SageFs removes from every process it starts, because it makes `dotnet` load the wrong SDK, so it was dropped. → Remove it from SessionEnv." name

  /// A user variable that has the same name as one SageFs sets. SageFs's value is the one the host gets.
  type Clash =
    { Name: string
      UserValue: string
      SageFsValue: string }

  let describeClash (clash: Clash) : string =
    sprintf "SessionEnv sets %s=%s, and SageFs sets it to %s for the host, so SageFs's value is used." clash.Name clash.UserValue clash.SageFsValue

  type Options =
    { Gc: GcMode
      /// The user's variables that passed the checks, in the order written, the last of a repeated name kept.
      Env: (string * string) list
      /// Extra arguments for the FSI host, after the ones SageFs builds.
      FsiArgs: string list }

  /// What a directory with no config gets. The same as before any option existed.
  let none : Options =
    { Gc = WorkstationGc
      Env = []
      FsiArgs = [] }

  let private invalidNameChars = [| '='; '\000' |]

  let private checkEnvName (name: string) : Result<string, Refusal> =
    match String.IsNullOrWhiteSpace name with
    | true -> Error EnvNameEmpty
    | false ->
      match name.IndexOfAny invalidNameChars >= 0 with
      | true -> Error(EnvNameInvalid name)
      | false ->
        match ProcessEnvironment.poisonedVariables |> List.exists (fun p -> String.Equals(p, name, StringComparison.Ordinal)) with
        | true -> Error(EnvNamePoisoned name)
        | false -> Ok name

  /// The options a directory's config asks for, and the entries that were refused (said, never silent).
  let ofConfig (config: DirectoryConfig) : Options * Refusal list =
    let checkedEnv = config.SessionEnv |> List.map (fun (name, value) -> checkEnvName name |> Result.map (fun n -> n, value))
    let refusedEnv = checkedEnv |> List.choose (function Error r -> Some r | Ok _ -> None)
    let keptEnv =
      checkedEnv
      |> List.choose (function Ok entry -> Some entry | Error _ -> None)
      |> List.rev
      |> List.distinctBy fst
      |> List.rev
    { Gc = config.Gc
      Env = keptEnv
      FsiArgs = config.DefaultArgs },
    refusedEnv

  /// The environment variables the choice of garbage collector needs. SageFs owns these names.
  let gcEnvironment (gc: GcMode) : (string * string) list =
    match gc with
    | ServerGc -> [ "DOTNET_gcServer", "1" ]
    | WorkstationGc -> []

  /// The host's environment: what SageFs built (`sageFsEnv`), then the collector's, then the user's, where a
  /// name SageFs sets keeps SageFs's value and is reported as a clash.
  let hostEnvironment (sageFsEnv: (string * string) list) (options: Options) : (string * string) list * Clash list =
    let owned = sageFsEnv @ gcEnvironment options.Gc
    let ownedValue name = owned |> List.tryFindBack (fun (n, _) -> String.Equals(n, name, StringComparison.Ordinal)) |> Option.map snd
    let clashes =
      options.Env
      |> List.choose (fun (name, value) ->
        match ownedValue name with
        | Some sageFsValue -> Some { Name = name; UserValue = value; SageFsValue = sageFsValue }
        | None -> None)
    let userOnly = options.Env |> List.filter (fun (name, _) -> (ownedValue name).IsNone)
    owned @ userOnly, clashes

  /// Who reads a field of the config. Every field of `DirectoryConfig` has a row, and a test fails when one does not.
  type Reader =
    /// The dashboard's create-session flow (the one client that consults `Load`).
    | DashboardCreate
    /// The warmup that opens namespaces, read when any client creates a session.
    | Warmup
    /// The live-bindings pane.
    | LiveValuePane
    /// `SessionOptions.ofConfig`: the FSI host's arguments, environment and builds.
    | SessionHost

  let fieldReaders : (string * Reader) list =
    [ "Load", DashboardCreate
      "AutoOpenNamespaces", Warmup
      "ValueWalk", LiveValuePane
      "DefaultArgs", SessionHost
      "Gc", SessionHost
      "SessionEnv", SessionHost ]
