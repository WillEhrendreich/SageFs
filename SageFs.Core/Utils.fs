module SageFs.Utils

//todo make instances
type ILogger =
  abstract member LogInfo: string -> unit
  abstract member LogDebug: string -> unit
  abstract member LogWarning: string -> unit
  abstract member LogError: string -> unit

/// Pluggable structured logging for SageFs.Core.
/// Defaults to stderr. Daemon wires to Microsoft.Extensions.Logging at startup
/// so everything flows through OTEL.
[<RequireQualifiedAccess>]
module Log =
  /// Mutable sinks — replaced at daemon startup with ILogger adapters.
  let mutable logInfo : string -> unit = fun s -> eprintfn "[INF] %s" s
  let mutable logDebug : string -> unit = fun s -> eprintfn "[DBG] %s" s
  let mutable logWarn : string -> unit = fun s -> eprintfn "[WRN] %s" s
  let mutable logError : string -> unit = fun s -> eprintfn "[ERR] %s" s

  /// Printf-style convenience — use these everywhere instead of eprintfn/printfn.
  let info fmt = Printf.kprintf logInfo fmt
  let debug fmt = Printf.kprintf logDebug fmt
  let warn fmt = Printf.kprintf logWarn fmt
  let error fmt = Printf.kprintf logError fmt

  /// Create a Utils.ILogger that delegates to the pluggable sinks.
  /// Used by existing code that takes ILogger as a parameter.
  let asILogger () : ILogger =
    { new ILogger with
        member _.LogInfo s = logInfo s
        member _.LogDebug s = logDebug s
        member _.LogWarning s = logWarn s
        member _.LogError s = logError s }

module Configuration =
  open System
  open System.IO
  open System.Reflection

  let getEmbeddedFileAsString fileName (asm: Assembly) =
    task {
      let stream = asm.GetManifestResourceStream fileName
      match isNull stream with
      | true ->
        let available = asm.GetManifestResourceNames() |> String.concat ", "
        return failwithf "Embedded resource '%s' not found in %s. Available: %s" fileName asm.FullName available
      | false ->
        use s = stream
        use reader = new StreamReader(s)
        return! reader.ReadToEndAsync()
    }

  let getBaseConfigString () =
    getEmbeddedFileAsString "SageFs.Core.base.fsx" (Assembly.GetExecutingAssembly())
