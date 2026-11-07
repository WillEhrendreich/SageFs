module SageFs.Tests.SessionOptionsTests

open System
open System.IO
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs

let private configWith (edit: DirectoryConfig -> DirectoryConfig) : DirectoryConfig = edit DirectoryConfigDefaults.empty

let private hostEnv (sageFsEnv: (string * string) list) (config: DirectoryConfig) =
  let options, _ = SessionOptions.ofConfig config
  SessionOptions.hostEnvironment sageFsEnv options

/// A name an environment variable may have, that is on nobody's refusal list.
let private genPlainName : Gen<string> =
  Gen.elements [ 'A' .. 'Z' ]
  |> Gen.listOfLength 6
  |> Gen.map (fun letters -> "USER_" + String(List.toArray letters))

let private genPoisonedName : Gen<string> = Gen.elements ProcessEnvironment.poisonedVariables

[<Tests>]
let sessionOptionsTests =
  testList "SessionOptions" [

    testCase "WHY - a Server GC choice puts DOTNET_gcServer=1 in the host's environment and Workstation does not" <| fun _ ->
      let server, _ = hostEnv [] (configWith (fun c -> { c with Gc = Server }))
      let workstation, _ = hostEnv [] (configWith (fun c -> { c with Gc = Workstation }))
      server |> List.contains ("DOTNET_gcServer", "1") |> Expect.isTrue "Server sets DOTNET_gcServer=1"
      workstation |> List.exists (fun (name, _) -> name = "DOTNET_gcServer") |> Expect.isFalse "Workstation sets nothing"

    testCase "WHY - a config that says nothing leaves the host's environment exactly as SageFs built it" <| fun _ ->
      let sageFs = [ "SAGEFS_PROJECT_OUTPUT", "/p/bin" ]
      let env, clashes = hostEnv sageFs DirectoryConfigDefaults.empty
      env |> Expect.equal "unchanged" sageFs
      clashes |> Expect.isEmpty "no clashes"

    testProperty "WHY - a session environment entry on the poisoned list is refused with its reason, and the rest are kept"
      (Prop.forAll (Arb.fromGen (Gen.zip (Gen.nonEmptyListOf genPoisonedName) (Gen.nonEmptyListOf genPlainName))) (fun (poisoned, plain) ->
        let plain = List.distinct plain
        let entries = (poisoned |> List.map (fun n -> n, "x")) @ (plain |> List.map (fun n -> n, "v"))
        let options, refusals = SessionOptions.ofConfig (configWith (fun c -> { c with SessionEnv = entries }))
        let refusedNames = refusals |> List.choose (function SessionOptions.EnvNamePoisoned n -> Some n | _ -> None) |> List.distinct |> List.sort
        refusedNames = (List.distinct poisoned |> List.sort)
        && (options.Env |> List.map fst |> List.sort) = (List.sort plain)))

    testCase "WHY - a refusal says what it refused and what to do" <| fun _ ->
      let text = SessionOptions.describe (SessionOptions.EnvNamePoisoned "DOTNET_ROOT")
      text |> Expect.stringContains "names the variable" "DOTNET_ROOT"
      text |> Expect.stringContains "gives the next step" "→"

    testCase "WHY - an empty variable name and one with an equals sign are refused" <| fun _ ->
      let _, refusals = SessionOptions.ofConfig (configWith (fun c -> { c with SessionEnv = [ "", "x"; "A=B", "y" ] }))
      refusals |> Expect.equal "both refused" [ SessionOptions.EnvNameEmpty; SessionOptions.EnvNameInvalid "A=B" ]

    testCase "WHY - a user variable that clashes with one SageFs sets is reported and SageFs's value wins" <| fun _ ->
      let config = configWith (fun c -> { c with SessionEnv = [ "SAGEFS_PROJECT_OUTPUT", "/mine"; "OTHER", "1" ] })
      let env, clashes = hostEnv [ "SAGEFS_PROJECT_OUTPUT", "/p/bin" ] config
      env |> List.filter (fun (n, _) -> n = "SAGEFS_PROJECT_OUTPUT") |> Expect.equal "one entry, SageFs's" [ "SAGEFS_PROJECT_OUTPUT", "/p/bin" ]
      env |> List.contains ("OTHER", "1") |> Expect.isTrue "the other entry is kept"
      clashes |> Expect.equal "reported" [ { SessionOptions.Name = "SAGEFS_PROJECT_OUTPUT"; UserValue = "/mine"; SageFsValue = "/p/bin" } ]

    testCase "WHY - Server GC wins over a user's DOTNET_gcServer=0 and the clash is reported" <| fun _ ->
      let config = configWith (fun c -> { c with Gc = Server; SessionEnv = [ "DOTNET_gcServer", "0" ] })
      let env, clashes = hostEnv [] config
      env |> List.filter (fun (n, _) -> n = "DOTNET_gcServer") |> Expect.equal "Server's value" [ "DOTNET_gcServer", "1" ]
      clashes |> List.map (fun c -> c.Name) |> Expect.equal "reported" [ "DOTNET_gcServer" ]

    testCase "WHY - DefaultArgs reach the arguments the FSI host is started with" <| fun _ ->
      let options, _ = SessionOptions.ofConfig (configWith (fun c -> { c with DefaultArgs = [ "--no-warn:1182" ] }))
      options.FsiArgs |> Expect.equal "carried" [ "--no-warn:1182" ]

    testCase "WHY - an option set for one directory does not reach another" <| fun _ ->
      let a, _ = SessionOptions.ofConfig (configWith (fun c -> { c with Gc = Server; SessionEnv = [ "A", "1" ] }))
      let b, _ = SessionOptions.ofConfig DirectoryConfigDefaults.empty
      a.Gc |> Expect.equal "a is Server" Server
      b |> Expect.equal "b is the default" SessionOptions.none

    testCase "WHY - every field a config file can set names who reads it, so a dead field fails the build" <| fun _ ->
      let fields =
        Reflection.FSharpType.GetRecordFields typeof<DirectoryConfig>
        |> Array.map (fun p -> p.Name)
        |> Array.sort
        |> Array.toList
      SessionOptions.fieldReaders |> List.map fst |> List.sort |> Expect.equal "table matches the record" fields

    testCase "WHY - a field the session host reads changes what the host gets, for each of them" <| fun _ ->
      let baseline = SessionOptions.ofConfig DirectoryConfigDefaults.empty |> fst
      let changes =
        [ "DefaultArgs", configWith (fun c -> { c with DefaultArgs = [ "--x" ] })
          "Gc", configWith (fun c -> { c with Gc = Server })
          "SessionEnv", configWith (fun c -> { c with SessionEnv = [ "A", "1" ] }) ]
      let hostFields =
        SessionOptions.fieldReaders
        |> List.choose (fun (name, reader) -> match reader with SessionOptions.SessionHost -> Some name | _ -> None)
        |> List.sort
      hostFields |> Expect.equal "the host-read fields" (changes |> List.map fst |> List.sort)
      for name, config in changes do
        (SessionOptions.ofConfig config |> fst) <> baseline |> Expect.isTrue (sprintf "%s changes the options" name)
  ]
