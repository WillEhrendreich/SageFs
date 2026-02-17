namespace SageFs.Gui

open Raylib_cs
open SageFs

/// Maps packed RGB uint32 values to Raylib Color.
module RaylibPalette =
  /// Convert packed RGB (0x00RRGGBB) to Raylib Color.
  let toColor (rgb: uint32) : Color =
    Color(Theme.rgbR rgb, Theme.rgbG rgb, Theme.rgbB rgb, 255uy)

  /// Convert hex string "#RRGGBB" to Raylib Color.
  let hexToColor (hex: string) : Color =
    toColor (Theme.hexToRgb hex)
