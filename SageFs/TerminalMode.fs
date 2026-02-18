module SageFs.Server.TerminalMode

open System
open SageFs

/// Set up the console for raw terminal input (alternate screen buffer)
let setupRawMode () =
  Console.TreatControlCAsInput <- true
  AnsiCodes.enableVT100 () |> ignore
  Console.Write(AnsiCodes.enterAltScreen)
  Console.Write(AnsiCodes.hideCursor)
  Console.Write(AnsiCodes.clearScreen)
  Console.Write(AnsiCodes.home)
  TerminalUIState.IsActive <- true

/// Restore console to normal mode (return to main screen buffer)
let restoreConsole () =
  TerminalUIState.IsActive <- false
  Console.TreatControlCAsInput <- false
  Console.Write(AnsiCodes.showCursor)
  Console.Write(AnsiCodes.reset)
  Console.Write(AnsiCodes.leaveAltScreen)
