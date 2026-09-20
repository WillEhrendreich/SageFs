module SageFs.Tests.FsiSessionContractTests

open System
open System.IO
open System.Threading
open Expecto
open Expecto.Flip
open FSharp.Compiler.Interactive.Shell
open SageFs.FsiSession
open SageFs.Tests.TestInfrastructure

/// A fresh in-process session behind the port.
let private newInProcess () : IFsiSession =
  let config = FsiEvaluationSession.GetDefaultConfiguration()
  let session =
    FsiEvaluationSession.Create(
      config,
      [| "fsi"; "--noninteractive"; "--nologo"; "--readline-" |],
      new StreamReader(Stream.Null),
      TextWriter.Null,
      TextWriter.Null,
      collectible = true
    )
  new InProcessFsiSession(session) :> IFsiSession

let private eval (session: IFsiSession) (code: string) = session.Eval(code, CancellationToken.None)

let private mustSucceed (session: IFsiSession) (code: string) =
  match (eval session code).Outcome with
  | FsiSucceeded -> ()
  | other -> failtestf "expected %s to succeed, got %A" code other

let private withSession (create: unit -> IFsiSession) (body: IFsiSession -> unit) =
  let session = create ()
  try body session
  finally session.Dispose()

/// The behaviour EVERY IFsiSession implementation must have. Instantiated for each implementation, so the
/// in-process and isolated-host sessions are held to one specification.
let contract (label: string) (create: unit -> IFsiSession) : Test =
  testList (sprintf "IFsiSession contract: %s" label) [
    testCase "a valid submission succeeds with no error diagnostics" <| fun _ ->
      withSession create (fun session ->
        let result = eval session "let x = 21 * 2;;"
        Expect.equal "succeeded" FsiSucceeded result.Outcome
        Expect.isEmpty "no diagnostics" result.Diagnostics)

    testCase "a type error fails and reports a diagnostic on line 1" <| fun _ ->
      withSession create (fun session ->
        let result = eval session "let y : int = \"not an int\";;"
        match result.Outcome with
        | FsiFailed _ -> ()
        | other -> failtestf "expected a failure, got %A" other
        Expect.isNonEmpty "at least one diagnostic" result.Diagnostics
        Expect.equal "first diagnostic starts on line 1" 1 result.Diagnostics.[0].Range.StartLine)

    testCase "state persists across submissions and BoundValue reads it" <| fun _ ->
      withSession create (fun session ->
        mustSucceed session "let x = 21 * 2;;"
        mustSucceed session "let y = x + 1;;"
        Expect.equal "x" (box 42) (session.BoundValue "x")
        Expect.equal "y" (box 43) (session.BoundValue "y")
        Expect.isNull "an unbound name is null" (session.BoundValue "nope"))

    testCase "ReadFlag distinguishes unbound, bool and not-a-bool" <| fun _ ->
      withSession create (fun session ->
        mustSucceed session "let flagOn = true;;"
        mustSucceed session "let flagOff = false;;"
        mustSucceed session "let notBool = 3;;"
        Expect.equal "unbound" FlagUnbound (session.ReadFlag "missing")
        Expect.equal "true" (FlagBound true) (session.ReadFlag "flagOn")
        Expect.equal "false" (FlagBound false) (session.ReadFlag "flagOff")
        match session.ReadFlag "notBool" with
        | FlagNotBool _ -> ()
        | other -> failtestf "expected FlagNotBool, got %A" other)

    testCase "LiveValuesJson lists the bound names and bumps the generation" <| fun _ ->
      withSession create (fun session ->
        mustSucceed session "let watched = 7;;"
        let generation = ref 0L
        let json = session.LiveValuesJson generation
        Expect.stringContains "names the binding" "watched" json
        Expect.equal "generation incremented" 1L generation.Value
        session.LiveValuesJson generation |> ignore
        Expect.equal "and again" 2L generation.Value)

    testCase "Completions offers List.map after 'List.ma'" <| fun _ ->
      withSession create (fun session ->
        let items = session.Completions("List.ma", 7, "ma")
        Expect.isTrue "map is offered" (items |> List.exists (fun item -> item.ReplacementText = "map")))

    testCase "Diagnose reports the same error the eval would" <| fun _ ->
      withSession create (fun session ->
        Expect.isNonEmpty "diagnostic for a type error" (session.Diagnose "let z : int = \"s\""))

    testCase "a disposed session can be disposed again" <| fun _ ->
      let session = create ()
      session.Dispose()
      session.Dispose()
  ]

[<Tests>]
let tests =
  testList "FsiSession port" [
    Integration.hostList "in-process session" [
      contract "InProcessFsiSession" newInProcess

      testCase "DynamicAssemblies exposes what FSI emitted (in-process only: hot reload reflects over these)" <| fun _ ->
        withSession newInProcess (fun session ->
          mustSucceed session "type Marker = { Value: int };;"
          Expect.isNonEmpty "an assembly was emitted" session.DynamicAssemblies)
    ]
  ]
