/// The daemon at 51.7GB and then 55GB of a 62GB box had no notion of "how
/// much room is left on this machine" — only its own working set. These
/// tests pin the pure `/proc/meminfo` parser `MachineMemory` builds that
/// number from: `MemAvailable` (not `MemFree`) in bytes, and a `None` for
/// anything that doesn't look like real meminfo content rather than a
/// misleading zero.
module SageFs.Tests.MachineMemoryTests

open Expecto
open Expecto.Flip
open SageFs.Features

[<Tests>]
let machineMemoryTests =
  testList "MachineMemory.parseMeminfo" [

    testCase "reads MemTotal and MemAvailable in bytes, kB -> bytes" <| fun () ->
      let lines =
        [| "MemTotal:       65712344 kB"
           "MemFree:         1234567 kB"
           "MemAvailable:   12345678 kB"
           "Buffers:          123456 kB" |]
      let expected : MachineMemory.Stats = { TotalBytes = 65712344L * 1024L; AvailableBytes = 12345678L * 1024L }
      MachineMemory.parseMeminfo lines
      |> Expect.equal "MemAvailable wins over MemFree, both converted from kB" (Some expected)

    testCase "a real /proc/meminfo shape (extra fields, varying whitespace) still parses" <| fun () ->
      let lines =
        [| "MemTotal:       65617464 kB"
           "MemFree:        21403632 kB"
           "MemAvailable:   30897924 kB"
           "Buffers:              56 kB"
           "Cached:         12204220 kB"
           "SwapTotal:      131234848 kB"
           "SwapFree:       125021980 kB" |]
      match MachineMemory.parseMeminfo lines with
      | None -> failtest "expected a parsed result"
      | Some stats ->
        stats.TotalBytes |> Expect.equal "MemTotal in bytes" (65617464L * 1024L)
        stats.AvailableBytes |> Expect.equal "MemAvailable in bytes" (30897924L * 1024L)

    testCase "missing MemAvailable yields None, never a fabricated number" <| fun () ->
      let lines = [| "MemTotal:       65712344 kB"; "MemFree:         1234567 kB" |]
      MachineMemory.parseMeminfo lines |> Expect.isNone "no MemAvailable line, no answer"

    testCase "missing MemTotal yields None" <| fun () ->
      let lines = [| "MemAvailable:   12345678 kB" |]
      MachineMemory.parseMeminfo lines |> Expect.isNone "no MemTotal line, no answer"

    testCase "garbage content yields None, not a zero-value Stats" <| fun () ->
      MachineMemory.parseMeminfo [| "this is not meminfo at all" |]
      |> Expect.isNone "unparseable lines never produce a fake reading"

    testCase "an unparseable numeric field yields None rather than a wrong number" <| fun () ->
      let lines = [| "MemTotal:       not-a-number kB"; "MemAvailable:   12345678 kB" |]
      MachineMemory.parseMeminfo lines |> Expect.isNone "a garbled MemTotal must not silently become some other number"

    testCase "current() never throws and always returns positive totals on this machine" <| fun () ->
      let stats = MachineMemory.current ()
      (stats.TotalBytes > 0L) |> Expect.isTrue "a real machine has a positive memory total"
      (stats.AvailableBytes >= 0L) |> Expect.isTrue "available is never negative"
  ]
