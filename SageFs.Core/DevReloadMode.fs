namespace SageFs

open System

/// Whether the browser dev-reload (live page refresh) is switched on. One rule, read by the worker's injector, by
/// `enable_hot_reload` and by the Settings page, so the three cannot say different things about one variable.
/// (This used to be a `DevReloadEnabled` flag in `SageFsConfig` that defaulted to off and that nothing read; the two
/// readers that existed treated everything but `0` and `false` as on.)
///
/// BCL only and in its own file, because the isolated FSI host compiles the injector and has none of `SageFsConfig`.
[<RequireQualifiedAccess>]
type DevReloadMode =
  | Enabled
  | Disabled

[<RequireQualifiedAccess>]
module DevReloadMode =
  [<Literal>]
  let envVar = "SAGEFS_DEVRELOAD"

  /// Unset is on. `0` and `false`, in any case and with spaces around them, are off. Anything else is on.
  let parse (value: string | null) : DevReloadMode =
    match value with
    | null -> DevReloadMode.Enabled
    | text ->
      match text.Trim().ToLowerInvariant() with
      | "0"
      | "false" -> DevReloadMode.Disabled
      | _ -> DevReloadMode.Enabled

  let fromEnvironment () : DevReloadMode =
    parse (SettingsInForce.read envVar)
