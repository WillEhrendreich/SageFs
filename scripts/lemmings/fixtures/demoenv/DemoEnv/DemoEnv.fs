// Pure parsing for the demo-recording environment variables that the Raylib
// samples honor: SAGEFS_DEMO_WINDOW (window position + size) and
// SAGEFS_DEMO_SEED (RNG seed), so recorded demos are reproducible.
//
// This module is intentionally side-effect-free: it takes the already-read
// environment value as a `string option` (None when the var is unset) and
// returns None whenever the value is missing or malformed.
module SageFs.Samples.DemoEnv

open System

/// A demo-recording window placement: pixel position and size on screen.
type WindowSpec =
  { X: int
    Y: int
    Width: int
    Height: int }

/// Parses "x,y,w,h" (e.g. "10,20,800,600") into a WindowSpec.
/// Returns None for a missing value, an empty/whitespace string, wrong arity,
/// non-integer components, or a non-positive width/height.
let parseWindow (raw: string option) : WindowSpec option =
  match raw with
  | None -> None
  | Some text ->
    match text.Trim() with
    | "" -> None
    | trimmed ->
      match trimmed.Split(',') with
      | [| xs; ys; ws; hs |] ->
        match Int32.TryParse xs, Int32.TryParse ys, Int32.TryParse ws, Int32.TryParse hs with
        | (true, x), (true, y), (true, w), (true, h) when w > 0 && h > 0 ->
          Some { X = x; Y = y; Width = w; Height = h }
        | _ -> None
      | _ -> None

/// Parses an integer RNG seed. Returns None for a missing or non-integer value.
let parseSeed (raw: string option) : int option =
  match raw with
  | None -> None
  | Some text ->
    match Int32.TryParse(text.Trim()) with
    | true, value -> Some value
    | false, _ -> None
