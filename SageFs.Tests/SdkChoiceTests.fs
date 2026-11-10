/// #183: a `net10.0` project on a machine with SDK 10 and SDK 11 installed and no `global.json` gets SDK 11, which
/// SageFs's .NET 10 worker cannot load into its own process. The project is read by hand and the session faults
/// in warmup. The choice is a pure function of the target framework, the installed SDKs and the pin, and the
/// refusal it returns names the SDK it chose and the `global.json` that fixes it.
module SageFs.Tests.SdkChoiceTests

open System
open System.IO
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.SdkChoice
open SageFs.Tests.SharedGenerators

let v (text: string) : SdkVersion =
  match SdkVersion.parse text with
  | Some version -> version
  | None -> failtestf "'%s' is not an SDK version" text

let sdks (texts: string list) : SdkVersion list = texts |> List.map v

/// The layout in the report: SDK 10 and SDK 11 installed, a net10.0 project, a worker on .NET 10, no pin.
let reported : Facts =
  { Need = AtLeastMajor 10
    HostMajor = 10
    Installed = sdks [ "10.0.201"; "11.0.100-rc.1.26425.128" ]
    Pin = NoPinFound }

let pinOf (path: string) (text: string) (policy: RollForward) : GlobalJsonPin =
  { Path = path; Version = v text; RollForward = policy }

let usedVersion (decision: Decision) : string =
  match decision with
  | Use pick -> pick.Sdk.Text
  | Refuse refusal -> failtestf "expected an SDK to use, got a refusal: %s" (describeRefusal refusal)

let refusalOf (decision: Decision) : Refusal =
  match decision with
  | Refuse refusal -> refusal
  | Use pick -> failtestf "expected a refusal, got SDK %s" pick.Sdk.Text

let tempDir () : string = Directory.CreateTempSubdirectory("sagefs-sdkchoice-").FullName

/// SDK versions the way machines have them: a few majors, feature bands 1 to 4, some previews.
let sdkGen : Gen<SdkVersion> =
  gen {
    let! major = Gen.choose (8, 12)
    let! patch = Gen.choose (100, 499)
    let! preview = Gen.elements [ false; false; true ]
    let text =
      match preview with
      | true -> sprintf "%d.0.%d-rc.1.26425.128" major patch
      | false -> sprintf "%d.0.%d" major patch
    return v text
  }

let factsGen : Gen<Facts> =
  gen {
    let! installed = Gen.listOfLength 4 sdkGen
    let! host = Gen.choose (9, 12)
    let! need = Gen.oneof [ Gen.constant AnySdk; Gen.map AtLeastMajor (Gen.choose (8, 12)) ]
    return { Need = need; HostMajor = host; Installed = installed; Pin = NoPinFound }
  }

[<Tests>]
let tests =
  testList "SdkChoice" [

    testList "version order" [
      testCase "WHY — a release outranks a pre-release of the same number, and a later pre-release outranks an earlier one" <| fun () ->
        (SdkVersion.compareVersions (v "11.0.100") (v "11.0.100-rc.2.26504.105") > 0) |> Expect.isTrue "release over rc"
        (SdkVersion.compareVersions (v "11.0.100-rc.2.26504.105") (v "11.0.100-rc.1.26425.128") > 0) |> Expect.isTrue "rc.2 over rc.1"
        (SdkVersion.compareVersions (v "11.0.100-rc.1.26425.128") (v "10.0.402") > 0) |> Expect.isTrue "any 11 over 10"

      testCase "WHY — dotnet --list-sdks output parses to its versions, and a line that is not a version is dropped" <| fun () ->
        let output =
          "10.0.402 [/home/will/.local/share/dotnetup/dotnet/sdk]\n11.0.100-rc.1.26425.128 [/home/will/.local/share/dotnetup/dotnet/sdk]\nnot a version line\n"
        parseSdkList output
        |> List.map (fun version -> version.Text)
        |> Expect.equal "both versions, in order" [ "10.0.402"; "11.0.100-rc.1.26425.128" ]
    ]

    testList "the choice" [
      testCase "WHY #183 — a net10.0 project with SDK 10 and SDK 11 installed, no global.json, on a .NET 10 worker is refused with the SDK it got and the one to pin" <| fun () ->
        match refusalOf (decide reported) with
        | TooNewForHost tooNew ->
          tooNew.Chosen.Sdk.Text |> Expect.equal "the newest installed SDK is what dotnet picks" "11.0.100-rc.1.26425.128"
          tooNew.Chosen.Source |> Expect.equal "nothing pins it" Newest
          tooNew.Remedy |> Expect.equal "the SDK that fits the worker and the project" (PinInstalled(v "10.0.201"))
        | other -> failtestf "expected TooNewForHost, got %A" other

      testCase "WHY #183 — the refusal text names the chosen SDK, the worker's runtime, and the exact global.json to write" <| fun () ->
        let text = describeRefusal (refusalOf (decide reported))
        text |> Expect.stringContains "names the SDK it chose" "11.0.100-rc.1.26425.128"
        text |> Expect.stringContains "says no global.json pinned it" "no global.json pins one"
        text |> Expect.stringContains "names the worker's runtime" ".NET 10"
        text |> Expect.stringContains "carries the exact JSON to write" (pinSnippet "10.0.201")
        text |> Expect.stringContains "says what to run next" "hard_reset_fsi_session"

      testCase "WHY — a worker on .NET 11 can load SDK 11, so the newest SDK is used and nothing is refused" <| fun () ->
        decide { reported with HostMajor = 11 } |> usedVersion |> Expect.equal "the newest installed" "11.0.100-rc.1.26425.128"

      testCase "WHY — a global.json pinning SDK 10 is honoured, and the pin is the reason" <| fun () ->
        let pin = pinOf "/repo/global.json" "10.0.201" RollForward.LatestPatch
        match decide { reported with Pin = PinFound pin } with
        | Use pick ->
          pick.Sdk.Text |> Expect.equal "the pinned SDK" "10.0.201"
          pick.Source |> Expect.equal "named as pinned by that file" (PinnedBy pin)
        | Refuse refusal -> failtestf "expected the pinned SDK, got: %s" (describeRefusal refusal)

      testCase "WHY — a global.json that pins the too-new SDK is the thing to change, and the text names that file" <| fun () ->
        let pin = pinOf "/repo/global.json" "11.0.100-rc.1.26425.128" RollForward.LatestPatch
        let text = describeRefusal (refusalOf (decide { reported with Pin = PinFound pin }))
        text |> Expect.stringContains "names the file that pins it" "/repo/global.json"
        text |> Expect.stringContains "says it is pinned" "pinned by /repo/global.json"
        text |> Expect.stringContains "gives the replacement" (pinSnippet "10.0.201")

      testCase "WHY — a project that needs SDK 11 on a .NET 10 worker is told no global.json can fix it, and is not offered an SDK that cannot build it" <| fun () ->
        let facts = { reported with Need = AtLeastMajor 11 }
        match refusalOf (decide facts) with
        | TooNewForHost tooNew ->
          tooNew.Remedy |> Expect.equal "SDK 10 cannot build a net11.0 project" NoInstalledFit
          describeRefusal (TooNewForHost tooNew) |> Expect.stringContains "says what to do instead" "Run SageFs on .NET 11 or newer"
        | other -> failtestf "expected TooNewForHost, got %A" other

      testCase "WHY — a release is suggested over a newer pre-release of the same major" <| fun () ->
        let facts = { reported with Installed = sdks [ "10.0.201"; "10.0.300-rc.1.26425.128"; "11.0.100" ] }
        match refusalOf (decide facts) with
        | TooNewForHost tooNew -> tooNew.Remedy |> Expect.equal "the release" (PinInstalled(v "10.0.201"))
        | other -> failtestf "expected TooNewForHost, got %A" other

      testCase "WHY — a pin no installed SDK satisfies is refused with what is installed" <| fun () ->
        let pin = pinOf "/repo/global.json" "9.0.100" RollForward.Disable
        match refusalOf (decide { reported with Pin = PinFound pin }) with
        | PinNotInstalled(refused, installed) ->
          refused |> Expect.equal "the pin" pin
          installed |> List.length |> Expect.equal "the installed list travels with it" 2
          describeRefusal (PinNotInstalled(refused, installed)) |> Expect.stringContains "lists what is installed" "10.0.201, 11.0.100-rc.1.26425.128"
        | other -> failtestf "expected PinNotInstalled, got %A" other

      testCase "WHY — no SDK installed at all is a refusal that says so" <| fun () ->
        refusalOf (decide { reported with Installed = [] }) |> Expect.equal "named" NoSdkInstalled
    ]

    testList "rollForward, by the documented rules" [
      let installed = sdks [ "9.0.305"; "10.0.201"; "10.0.203"; "10.0.301"; "10.1.100"; "11.0.100" ]
      let pick (text: string) (policy: RollForward) : string option =
        resolvePin (pinOf "g" text policy) installed |> Option.map (fun s -> s.Text)
      let cases =
        [ "latestPatch is the default and stays in the feature band", "10.0.201", RollForward.LatestPatch, Some "10.0.203"
          "patch takes the exact version when it is installed", "10.0.201", RollForward.Patch, Some "10.0.201"
          "patch rolls to the newest patch of the band when the exact one is missing", "10.0.202", RollForward.Patch, Some "10.0.203"
          "disable takes only the exact version", "10.0.202", RollForward.Disable, None
          "feature moves to the lowest higher feature band and takes its newest patch", "10.0.250", RollForward.Feature, Some "10.0.301"
          "latestFeature takes the newest of the minor", "10.0.201", RollForward.LatestFeature, Some "10.0.301"
          "minor moves to the lowest higher minor", "10.0.400", RollForward.Minor, Some "10.1.100"
          "latestMinor takes the newest of the major", "10.0.201", RollForward.LatestMinor, Some "10.1.100"
          "major moves to the lowest higher major", "10.2.100", RollForward.Major, Some "11.0.100"
          "latestMajor takes the newest installed", "9.0.305", RollForward.LatestMajor, Some "11.0.100"
          "a pin above everything installed finds nothing", "12.0.100", RollForward.LatestMajor, None ]
      for name, version, policy, expected in cases do
        testCase (sprintf "WHY — %s (pin %s, %s)" name version (RollForward.describe policy)) <| fun () ->
          pick version policy |> Expect.equal "the installed SDK it selects" expected
    ]

    testList "global.json" [
      testCase "WHY — a global.json with comments and a trailing comma still parses, and a missing rollForward is latestPatch" <| fun () ->
        let text = "{\n  // pin\n  \"sdk\": { \"version\": \"10.0.201\", },\n}"
        match parseGlobalJson "/repo/global.json" text with
        | Result.Ok(PinFound pin) ->
          pin.Version.Text |> Expect.equal "version" "10.0.201"
          pin.RollForward |> Expect.equal "default policy" RollForward.LatestPatch
          pin.Path |> Expect.equal "path" "/repo/global.json"
        | other -> failtestf "expected a pin, got %A" other

      testCase "WHY — a global.json that only carries msbuild-sdks pins nothing" <| fun () ->
        parseGlobalJson "g" "{ \"msbuild-sdks\": { \"X\": \"1.0.0\" } }" |> Expect.equal "no pin" (Result.Ok NoPinFound)

      testCase "WHY — an unparseable version or an unknown rollForward is a named error that says which file" <| fun () ->
        let cases =
          [ "{ \"sdk\": { \"version\": \"latest\" } }", VersionNotParsable("/repo/global.json", "latest")
            "{ \"sdk\": { \"version\": \"10.0.201\", \"rollForward\": \"sideways\" } }", RollForwardUnknown("/repo/global.json", "sideways") ]
        for text, expected in cases do
          parseGlobalJson "/repo/global.json" text |> Expect.equal (sprintf "the error for %s" text) (Result.Error expected)
          describeGlobalJsonError expected |> Expect.stringContains "names the file" "/repo/global.json"
        match parseGlobalJson "/repo/global.json" "{ nope" with
        | Result.Error(NotJson(path, _)) -> path |> Expect.equal "names the file" "/repo/global.json"
        | other -> failtestf "expected NotJson, got %A" other

      testCase "WHY — the nearest global.json decides, even when it pins nothing, because dotnet stops at the first one it finds" <| fun () ->
        let root = tempDir ()
        try
          let inner = Path.Combine(root, "src", "App")
          Directory.CreateDirectory inner |> ignore
          File.WriteAllText(Path.Combine(root, "global.json"), "{ \"sdk\": { \"version\": \"10.0.201\" } }")
          match findPin inner with
          | PinFound pin -> pin.Version.Text |> Expect.equal "the outer pin is found from below" "10.0.201"
          | NoPinFound -> failtest "expected the outer pin"
          File.WriteAllText(Path.Combine(root, "src", "global.json"), "{ \"msbuild-sdks\": {} }")
          findPin inner |> Expect.equal "the nearer file pins nothing, so nothing is pinned" NoPinFound
        finally
          Directory.Delete(root, true)
    ]

    testList "the project's target framework" [
      testCase "WHY — the highest major among the declared frameworks of every project is what the SDK must be able to build" <| fun () ->
        let root = tempDir ()
        try
          let write name tfmElement =
            let path = Path.Combine(root, name)
            File.WriteAllText(path, sprintf "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>%s</PropertyGroup></Project>" tfmElement)
            path
          let single = write "A.fsproj" "<TargetFramework>net10.0</TargetFramework>"
          let multi = write "B.fsproj" "<TargetFrameworks>net8.0;net11.0</TargetFrameworks>"
          let none = write "C.fsproj" "<OutputType>Exe</OutputType>"
          sdkNeedOf [ single ] |> Expect.equal "single" (AtLeastMajor 10)
          sdkNeedOf [ multi ] |> Expect.equal "multi" (AtLeastMajor 11)
          sdkNeedOf [ single; multi ] |> Expect.equal "highest across projects" (AtLeastMajor 11)
          sdkNeedOf [ none ] |> Expect.equal "a project that names none adds nothing" AnySdk
          sdkNeedOf [ Path.Combine(root, "Missing.fsproj") ] |> Expect.equal "an unreadable project adds nothing" AnySdk
        finally
          Directory.Delete(root, true)
    ]

    testList "the warmup fault" [
      let skipped = ProjectLoading.LoadMode.ManualFallback(ProjectLoading.FallbackCause.SkippedNewerSdk(11, 10))
      let refusal = refusalOf (decide reported)

      testCase "WHY #183 — a project skipped for a newer SDK adds the refusal to the fault, with the SDK and the global.json" <| fun () ->
        match faultAdvice (fun () -> Some refusal) skipped with
        | Some text ->
          text |> Expect.stringContains "names the SDK" "11.0.100-rc.1.26425.128"
          text |> Expect.stringContains "gives the pin" (pinSnippet "10.0.201")
        | None -> failtest "expected advice"

      testCase "WHY — when the machine cannot be asked, the skip's own description is still there, so the fault names a cause" <| fun () ->
        match faultAdvice (fun () -> None) skipped with
        | Some text -> text |> Expect.equal "the cause's description" (ProjectLoading.FallbackCause.describe (ProjectLoading.FallbackCause.SkippedNewerSdk(11, 10)))
        | None -> failtest "expected the cause's description"

      testCase "WHY #183 — the status line for a skipped newer SDK says to pin an older one in a global.json, not only to run dotnet build" <| fun () ->
        ProjectLoading.FallbackCause.describe (ProjectLoading.FallbackCause.SkippedNewerSdk(11, 10))
        |> Expect.stringContains "names the file that fixes it" "global.json"

      testCase "WHY — any other fallback adds its own description, and the machine is not asked" <| fun () ->
        let cause = ProjectLoading.FallbackCause.Threw "MSB4019: the imported project was not found"
        let asked = ref false
        match faultAdvice (fun () -> asked.Value <- true; None) (ProjectLoading.LoadMode.ManualFallback cause) with
        | Some text -> text |> Expect.stringContains "carries the MSBuild message" "MSB4019"
        | None -> failtest "expected the cause's description"
        asked.Value |> Expect.isFalse "no SDK question for a loader failure"

      testCase "WHY #183 — the fault keeps the compiler's own message and puts the project-load cause under it" <| fun () ->
        let message = faultMessage "Initial warm-up failed: Error creating evaluation session: StopProcessingExn" (faultAdvice (fun () -> Some refusal) skipped)
        message |> Expect.stringContains "the compiler's message is kept" "StopProcessingExn"
        message |> Expect.stringContains "the SDK it chose" "11.0.100-rc.1.26425.128"
        message |> Expect.stringContains "the global.json to write" (pinSnippet "10.0.201")
        faultMessage "boom" None |> Expect.equal "no cause, no addition" "boom"

      testCase "WHY — a project the loader evaluated adds nothing" <| fun () ->
        faultAdvice (fun () -> failtest "must not ask") ProjectLoading.LoadMode.Evaluated |> Expect.isNone "nothing to add"

      testCase "WHY #183 — the published warmup failure keeps its own text and adds the load cause; an evaluated project's is unchanged" <| fun () ->
        let message = "Initial warm-up failed: Error creating evaluation session: StopProcessingExn"
        let threw = ProjectLoading.LoadMode.ManualFallback(ProjectLoading.FallbackCause.Threw "MSB4019: the imported project was not found")
        let published = warmupFailureMessage 10 message threw [ "/work/App/App.fsproj" ]
        published |> Expect.stringStarts "the failure's own text leads" message
        published |> Expect.stringContains "the MSBuild message is under it" "MSB4019"
        warmupFailureMessage 10 message ProjectLoading.LoadMode.Evaluated [ "/work/App/App.fsproj" ]
        |> Expect.equal "nothing added" message
    ]

    testList "the create reply" [
      let refusal = refusalOf (decide reported)

      testCase "WHY #183 — a project session's reply carries the SDK line, asked with the project, the working directory and the worker's major" <| fun () ->
        let asked : (int * string * string list) option ref = ref None
        let ask (hostMajor: int) (dir: string) (projects: string list) =
          asked.Value <- Some(hostMajor, dir, projects)
          Some refusal
        match CreateSessionUx.sdkHintWith ask 10 "/work/repo" [ SessionProjectTarget.Project "/work/repo/App/App.fsproj" ] with
        | Some line ->
          line |> Expect.stringStarts "labelled as the SDK line" "SDK: "
          line |> Expect.stringContains "gives the pin" (pinSnippet "10.0.201")
        | None -> failtest "expected an SDK line"
        asked.Value |> Expect.equal "what the edge was asked" (Some(10, "/work/repo", [ "/work/repo/App/App.fsproj" ]))

      testCase "WHY — a bare session has no project, so the machine is not asked and no line is added" <| fun () ->
        CreateSessionUx.sdkHintWith (fun _ _ _ -> failtest "must not ask") 10 "/work" [ SessionProjectTarget.Bare ]
        |> Expect.isNone "no SDK line"

      testCase "WHY — a usable SDK adds no line" <| fun () ->
        CreateSessionUx.sdkHintWith (fun _ _ _ -> None) 10 "/work" [ SessionProjectTarget.Project "/work/A.fsproj" ]
        |> Expect.isNone "nothing to say"
    ]

    testList "properties" [
      testPropertyWithConfig propConfig "WHY — an SDK the worker cannot load is never the one to use, and a suggestion is always installed, loadable and able to build the project"
        (Prop.forAll (Arb.fromGen factsGen) (fun (facts: Facts) ->
          match decide facts with
          | Use pick -> pick.Sdk.Major <= facts.HostMajor
          | Refuse(TooNewForHost tooNew) ->
            tooNew.Chosen.Sdk.Major > facts.HostMajor
            && (match tooNew.Remedy with
                | NoInstalledFit -> true
                | PinInstalled s ->
                  List.contains s facts.Installed
                  && s.Major <= facts.HostMajor
                  && (match facts.Need with
                      | AtLeastMajor needed -> s.Major >= needed
                      | AnySdk -> true))
          | Refuse _ -> false))
    ]
  ]
