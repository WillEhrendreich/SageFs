module SageFs.Tests.SharedGenerators

open Expecto
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.WorkerProtocol

/// Shared FsCheck config: 200 random inputs per property
let propConfig = { FsCheckConfig.defaultConfig with maxTest = 200 }

/// Lighter config for expensive generators
let lightConfig = { FsCheckConfig.defaultConfig with maxTest = 100 }

// ── Session / Status ──

/// Adversarial build-reason strings: empty, parens, nested prefix, unicode, random
let genBuildReason =
  Gen.oneof [
    Gen.constant ""
    Gen.constant "dotnet build"
    Gen.constant "rebuild"
    Gen.constant "hot reload"
    Gen.constant "with (parens)"
    Gen.constant "Building (nested)"
    Gen.constant "reason with ) embedded"
    Gen.constant "日本語 build"
    (Gen.elements ['a'..'z']
     |> Gen.listOfLength 20
     |> Gen.map (fun cs -> System.String(cs |> List.toArray)))
  ]

/// All SessionStatus cases including Building with adversarial reasons
let genSessionStatus =
  Gen.frequency [
    1, Gen.constant SessionStatus.Starting
    1, Gen.constant SessionStatus.Ready
    1, Gen.constant SessionStatus.Evaluating
    1, Gen.constant SessionStatus.Faulted
    1, Gen.constant SessionStatus.Restarting
    1, Gen.constant SessionStatus.Stopped
    3, genBuildReason |> Gen.map SessionStatus.Building
  ]

// ── Geometry ──

let genSmallRect =
  gen {
    let! w = Gen.choose (1, 40)
    let! h = Gen.choose (1, 30)
    let! row = Gen.choose (0, 50)
    let! col = Gen.choose (0, 80)
    return Rect.create row col w h
  }

let genCellAttrs =
  Gen.elements [
    CellAttrs.None; CellAttrs.Bold; CellAttrs.Dim
    CellAttrs.Inverse
  ]

let genCell =
  gen {
    let! ch = Gen.elements (['A'..'Z'] @ ['a'..'z'] @ [' '; '.'; '|'])
    let! fg = Gen.choose (0, 0xFFFFFF) |> Gen.map uint32
    let! bg = Gen.choose (0, 0xFFFFFF) |> Gen.map uint32
    let! attrs = genCellAttrs
    return Cell.create ch fg bg attrs
  }

// ── Test SessionId helpers ──

/// Create a SessionId for testing from a known-valid 8-char hex string.
let testSessionId (hex: string) =
  match SessionId.validate hex with
  | Ok sid -> sid
  | Error e -> failwithf "test bug: invalid session ID '%s': %s" hex e

/// FsCheck generator for valid SessionIds.
let genSessionId =
  gen {
    let! chars = Gen.listOfLength 8 (Gen.elements (['0'..'9'] @ ['a'..'f']))
    return testSessionId (System.String(chars |> List.toArray))
  }

// ── Colors ──

let genHexColor =
  gen {
    let! r = Gen.choose (0, 255)
    let! g = Gen.choose (0, 255)
    let! b = Gen.choose (0, 255)
    return sprintf "#%02x%02x%02x" r g b
  }

let genRgbComponents =
  gen {
    let! r = Gen.choose (0, 255) |> Gen.map byte
    let! g = Gen.choose (0, 255) |> Gen.map byte
    let! b = Gen.choose (0, 255) |> Gen.map byte
    return (r, g, b)
  }
