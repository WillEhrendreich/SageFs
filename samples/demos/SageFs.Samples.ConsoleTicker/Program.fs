// ============================================================
//  ⏱️  Console Ticker Demo
//  Prints one line every `intervalMs`, forever. Built for the SageFs demo
//  recordings (demo-gif-plan.md §7 "G2") — a large-font-friendly console
//  app whose output keeps changing so a hot-reload edit always lands on a
//  visibly live line.
//
//  To run this demo:
//    1. Start SageFs:  sagefs
//    2. Watch this project, run it, and watch the output
//    3. Edit `renderLine` in Ticker.fs (the message or the format), save —
//       the next tick shows the change, no restart.
//
//  The counter is also the reference example for REGISTERED state: it lives in
//  a `Holder` the app registers, so a type-shape edit can be answered with
//  "a live value of the old shape exists, so rebuild" rather than by guessing.
//  See docs/granular-restart-scope.md.
//
//  Configuration:
//    SAGEFS_TICKER_INTERVAL_MS — milliseconds between lines (default 1000)
// ============================================================
module SageFs.Samples.ConsoleTicker.Program

open System
open System.Threading
open SageFs
open SageFs.Samples.ConsoleTicker.Ticker

let private readEnv (name: string) : string option =
  match Environment.GetEnvironmentVariable name with
  | null -> None
  | value -> Some value

[<EntryPoint>]
let main _args =
  let intervalMs = readEnv "SAGEFS_TICKER_INTERVAL_MS" |> parseIntervalMs

  // The counter is the app's LIVE STATE, so it lives in a REGISTERED holder
  // rather than a bare `let mutable`. That is the whole point of registering
  // it: SageFs can now answer "does anything hold a value of the old shape?"
  // when a type changes, instead of being unable to say and paying a rebuild on
  // faith. `let mutable n` was correct; this is the same correctness made
  // visible to a restart.
  //
  // The cell's value is a `ref`, so a write through it is seen by every reader
  // — verified by running this app: 1 2 3, then a read back of 3.
  let registry = HolderRegistry.New ()
  let heldTicks = RegisteredHolder.holdRegistered registry "TickerState" 0
  let ticks = RegisteredHolder.cellOf heldTicks

  let nextTick () =
    let current = Holder.read ticks
    ticks.Value.Value <- current + 1
    current + 1

  while true do
    Console.Out.WriteLine(renderLine (nextTick ()) DateTime.Now)
    Console.Out.Flush()
    Thread.Sleep intervalMs
  0
