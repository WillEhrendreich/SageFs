namespace SageFs.Gui

open Raylib_cs

#nowarn "3391" // implicit CBool -> bool conversion from Raylib-cs

/// Thin F#-idiomatic wrappers over Raylib-cs.
/// Only inline functions — zero overhead, just readability.
[<AutoOpen>]
module Rl =
  /// Get next key from the queue as KeyboardKey (0-cost enum cast).
  let inline keyPressed () : KeyboardKey =
    Raylib.GetKeyPressed() |> enum<KeyboardKey>

  /// Get next char from the input queue (0 = empty).
  let inline charPressed () : int = Raylib.GetCharPressed()

  /// Is a specific key currently held down?
  let inline isDown (key: KeyboardKey) : bool = Raylib.IsKeyDown(key)

  let inline ctrl () = isDown KeyboardKey.LeftControl || isDown KeyboardKey.RightControl
  let inline alt () = isDown KeyboardKey.LeftAlt || isDown KeyboardKey.RightAlt
  let inline shift () = isDown KeyboardKey.LeftShift || isDown KeyboardKey.RightShift

  let inline screenW () = Raylib.GetScreenWidth()
  let inline screenH () = Raylib.GetScreenHeight()
  let inline fps () = Raylib.GetFPS()

  let inline windowShouldClose () : bool = Raylib.WindowShouldClose()
  let inline windowReady () : bool = Raylib.IsWindowReady()
