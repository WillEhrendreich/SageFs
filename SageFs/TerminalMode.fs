module SageFs.Server.TerminalMode

open System
open SageFs

/// Set up the console for raw terminal input (alternate screen buffer + mouse)
let setupRawMode () =
  Console.TreatControlCAsInput <- true
  AnsiCodes.enableVT100 () |> ignore
  AnsiCodes.enableVtInput () |> ignore
  Console.Write(AnsiCodes.enterAltScreen)
  Console.Write(AnsiCodes.hideCursor)
  Console.Write(AnsiCodes.enableMouse)
  Console.Write(AnsiCodes.clearScreen)
  Console.Write(AnsiCodes.home)
  TerminalUIState.IsActive <- true

/// Restore console to normal mode (return to main screen buffer, disable mouse)
let restoreConsole () =
  TerminalUIState.IsActive <- false
  Console.TreatControlCAsInput <- false
  Console.Write(AnsiCodes.disableMouse)
  Console.Write(AnsiCodes.showCursor)
  Console.Write(AnsiCodes.reset)
  Console.Write(AnsiCodes.leaveAltScreen)
  AnsiCodes.restoreVtInput ()
