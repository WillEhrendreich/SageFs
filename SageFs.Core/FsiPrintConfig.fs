// The session configuration that applies `EvalPrintSettings` (docs/plans/eval-output-for-an-llm.md).
//
// Compiled into SageFs.Core and into the isolated FSI host, so the two creation sites cannot disagree. It needs the
// compiler service and PrintLimits, nothing else. The host builds against the project's own SDK, so this touches only
// members that every FSharp.Compiler.Service version has had since the host configuration type appeared.
module SageFs.FsiPrintConfig

open FSharp.Compiler.Interactive.Shell

/// `baseConfig` with its printing replaced by `settings`. Everything else (the event loop, the formatting culture,
/// added printers) is the base's own.
let apply (settings: EvalPrintSettings) (baseConfig: FsiEvaluationSessionHostConfig) : FsiEvaluationSessionHostConfig =
  { new FsiEvaluationSessionHostConfig() with
      member _.FormatProvider = baseConfig.FormatProvider
      member _.FloatingPointFormat = baseConfig.FloatingPointFormat
      member _.AddedPrinters = baseConfig.AddedPrinters
      member _.ShowDeclarationValues = baseConfig.ShowDeclarationValues
      member _.ShowIEnumerable = baseConfig.ShowIEnumerable
      member _.ShowProperties = baseConfig.ShowProperties
      member _.PrintSize = baseConfig.PrintSize
      member _.PrintDepth = baseConfig.PrintDepth
      member _.PrintWidth = baseConfig.PrintWidth
      member _.PrintLength = baseConfig.PrintLength
      member _.ReportUserCommandLineArgs args = baseConfig.ReportUserCommandLineArgs args
      member _.StartServer name = baseConfig.StartServer name
      member _.EventLoopRun() = baseConfig.EventLoopRun()
      member _.EventLoopInvoke f = baseConfig.EventLoopInvoke f
      member _.EventLoopScheduleRestart() = baseConfig.EventLoopScheduleRestart()
      member _.UseFsiAuxLib = baseConfig.UseFsiAuxLib
      member _.GetOptionalConsoleReadLine probe = baseConfig.GetOptionalConsoleReadLine probe }

/// The configuration both session kinds start from: the compiler service's default, printing under `settings`.
let forSession (settings: EvalPrintSettings) : FsiEvaluationSessionHostConfig =
  apply settings (FsiEvaluationSession.GetDefaultConfiguration())
