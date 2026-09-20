// WHY — `sagefs.density` ships three values with three `enumDescriptions` that
// read like a contract, and exactly ONE surface honoured it
// (sagefs-ux-roast.md §4.3). In `minimal` — documented as *"Only inline
// results on eval, nothing persistent"* — the user still got test gutter
// signs, coverage gutters, two CodeLens families and persistent binding ghost
// text. The setting described a product that did not exist.
//
// These pin the table against package.json's own enum and descriptions, so the
// promise and the behaviour cannot drift apart again.
//
// Runs under plain `dotnet fsi` (no Fable), mirroring AppRunContractTests.fsx.
#r "nuget: Expecto, 11.0.0-alpha8"
#load "../src/DensityPure.fs"

open System.IO
open System.Text.Json
open Expecto
open Expecto.Flip
open SageFs.Vscode.DensityPure

let private pkg =
  JsonDocument.Parse(File.ReadAllText(Path.Combine(__SOURCE_DIRECTORY__, "..", "package.json"))).RootElement

let private densitySetting =
  pkg.GetProperty("contributes").GetProperty("configuration").GetProperty("properties").GetProperty("sagefs.density")

let private enumValues =
  densitySetting.GetProperty("enum").EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> List.ofSeq

let private enumDescriptions =
  densitySetting.GetProperty("enumDescriptions").EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> List.ofSeq

let private descriptionFor (d: Density) =
  List.zip enumValues enumDescriptions
  |> List.tryFind (fun (v, _) -> v = Density.toString d)
  |> Option.map snd
  |> Option.defaultValue ""

let tests =
  testList "VS Code density - the setting's promise is the behaviour" [

    testCase "WHY - the DU covers exactly package.json's enum, so neither can gain a value alone" <| fun _ ->
      enumValues |> Expect.isNonEmpty "the setting was found in package.json"
      allDensities |> List.map Density.toString |> Expect.equal "same values, same order" enumValues
      enumDescriptions.Length |> Expect.equal "one description per value" enumValues.Length

    testCase "WHY - minimal keeps inline eval results and NOTHING else, which is what it promises" <| fun _ ->
      descriptionFor Density.Minimal
      |> Expect.stringContains "the promise is still worded that way" "nothing persistent"
      shows Density.Minimal AnnotationSurface.InlineEvalResult |> Expect.isTrue "the one thing it keeps"
      allSurfaces
      |> List.filter (fun s -> s <> AnnotationSurface.InlineEvalResult)
      |> List.filter (shows Density.Minimal)
      |> Expect.isEmpty "nothing persistent means nothing persistent"

    testCase "WHY - normal keeps test signs and drops code lens, exactly as its description says" <| fun _ ->
      let desc = descriptionFor Density.Normal
      desc |> Expect.stringContains "still promises test signs" "test signs"
      desc |> Expect.stringContains "still promises no code lens" "no code lens"
      shows Density.Normal AnnotationSurface.TestSigns |> Expect.isTrue "test signs kept"
      shows Density.Normal AnnotationSurface.CoverageGutters |> Expect.isTrue "the other gutter too"
      shows Density.Normal AnnotationSurface.EvalCodeLens |> Expect.isFalse "eval lens dropped"
      shows Density.Normal AnnotationSurface.TestCodeLens |> Expect.isFalse "test lens dropped"
      shows Density.Normal AnnotationSurface.CoverageCodeLens |> Expect.isFalse "coverage lens dropped"
      shows Density.Normal AnnotationSurface.CellHighlight |> Expect.isFalse "cell highlight dropped"

    testCase "WHY - full draws everything, because that is the whole description" <| fun _ ->
      allSurfaces |> List.filter (shows Density.Full >> not)
      |> Expect.isEmpty "full hides nothing"

    testCase "WHY - the presets are monotonic: nothing reappears as you turn the dial down" <| fun _ ->
      // A surface hidden at `normal` must stay hidden at `minimal`. Without
      // this, "turn it down" would not be a dial at all.
      allSurfaces
      |> List.filter (fun s -> shows Density.Minimal s && not (shows Density.Normal s))
      |> Expect.isEmpty "minimal never shows what normal hides"
      allSurfaces
      |> List.filter (fun s -> shows Density.Normal s && not (shows Density.Full s))
      |> Expect.isEmpty "normal never shows what full hides"

    testCase "WHY - the parse is total and an unknown value falls back to the documented default" <| fun _ ->
      Density.ofString "minimal" |> Expect.equal "minimal" Density.Minimal
      Density.ofString "NORMAL" |> Expect.equal "case-insensitive" Density.Normal
      Density.ofString "wat" |> Expect.equal "the package.json default" Density.Full
      Density.ofString null |> Expect.equal "null is full" Density.Full
      densitySetting.GetProperty("default").GetString()
      |> Expect.equal "and that default is still what package.json says" "full"

    testCase "WHY - the cycle visits every preset and returns, so the command cannot strand a user" <| fun _ ->
      let visited =
        List.fold (fun (acc, cur) _ -> (cur :: acc, Density.next cur)) ([], Density.Full) [ 1; 2; 3 ]
        |> fst
      visited |> List.distinct |> List.length |> Expect.equal "all three visited" 3
      Density.next (Density.next (Density.next Density.Full))
      |> Expect.equal "and it comes back round" Density.Full
  ]

let argv = System.Environment.GetCommandLineArgs() |> Array.skipWhile (fun a -> not (a.EndsWith ".fsx")) |> Array.skip 1
exit (runTestsWithCLIArgs [] argv tests)
