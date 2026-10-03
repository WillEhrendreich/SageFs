/// Tests for the WHOLE-SURFACE authority gate: `Affordances.ToolName` (the
/// closed tool-name DU), `Affordances.ToolRole` (what this project means by
/// `Observer`), `Affordances.AuthorityRefusal` (the named refusal DU) and
/// `Affordances.checkAuthorityAllowed` (the gate itself).
///
/// WHY THIS FILE EXISTS. `cohortTools` used to be the only authority
/// dimension and it spoke about ten cohort verbs; every other tool name fell
/// through it onto the session-state gate, which knows nothing about roles.
/// A member whose role is `Observer` could then call `send_fsharp_code`,
/// `hard_reset_fsi_session` and `run_app`. These tests pin the replacement in
/// both directions: the refusing cases AND an accepting case for every role,
/// because a gate that always refuses passes a "refuses" test and is not a
/// gate.
///
/// Pure functions only — no daemon, no `CohortOwner`, no ledger. The live
/// wiring through `admitToolCallWithin` lives in the MCP integration tests.
module SageFs.Tests.ToolAuthorityGateTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.Cohort
open SageFs.MemberTable

let private alice = MemberId.Minted "alice"
let private bob = MemberId.Minted "bob"

let private observer = Authority.Member(alice, JoinableRole.Observer)
let private verifier = Authority.Member(alice, JoinableRole.Verifier)
let private implementer = Authority.Member(alice, JoinableRole.Implementer)
let private conductor = Authority.Conductor alice

/// The three tools the defect was about, named once.
let private theThree: Affordances.ToolName list =
  [ Affordances.ToolName.SendFsharpCode
    Affordances.ToolName.HardResetFsiSession
    Affordances.ToolName.RunApp ]

[<Tests>]
let toolAuthorityGateTests =
  testList "Tool authority gate (whole tool surface)" [

    // ── The defect, pinned in the refusing direction ────────────────────

    testList "Observer cannot run, build, reset or delete" [
      test "an Observer is REFUSED send_fsharp_code — the eval that motivated this gate" {
        // THE regression. Before the whole-surface gate, `checkCohortAuthorityGate`
        // returned `None` for this tool name, the call fell through to the
        // session-state gate, and a Ready session admitted it for any role.
        Affordances.checkAuthorityAllowed observer Affordances.ToolName.SendFsharpCode
        |> Expect.equal
          "an Observer may not evaluate arbitrary F#"
          (Error(Affordances.AuthorityRefusal.RoleForbids(Affordances.ToolName.SendFsharpCode, Affordances.ToolRole.Working)))
      }

      test "all three of the tools the defect named are refused for an Observer" {
        for tool in theThree do
          Affordances.checkAuthorityAllowed observer tool
          |> Expect.equal
            (sprintf "an Observer may not call %s" (Affordances.ToolName.toString tool))
            (Error(Affordances.AuthorityRefusal.RoleForbids(tool, Affordances.ToolRole.Working)))
      }

      test "but an UNJOINED caller is NOT locked out of its own sessions" {
        // The regression this whole gate shipped with: `Anonymous` mapped to `Observer`, so on
        // an empty frame `send_fsharp_code`, `run_app` and `hard_reset_fsi_session` were refused
        // for a caller that had never joined anything. The moment ONE agent formed a cohort, every
        // other MCP client — the user in their editor, a dashboard tab, a second agent — was locked
        // out of every code tool, and the refusal's own next action ("mint you a token") is only
        // reachable BY THE CONDUCTOR, so a solo user could never mint their way out.
        //
        // `Anonymous` is now `Working`: a caller with no seat has no role to narrow it, and the
        // role gate governs a caller's OWN session. It is still refused every COHORT verb, which
        // the next case pins — so this does not become a way around the cohort gate.
        for tool in theThree do
          Affordances.checkAuthorityAllowed Authority.Anonymous tool
          |> Expect.equal
            (sprintf "an unjoined caller may still use %s on its own session" (Affordances.ToolName.toString tool))
            (Ok ())
      }

      test "and an unjoined caller still gets NO cohort authority, which is what the seat is for" {
        // The other half of the case above, and the reason `Anonymous = Working` is not a hole:
        // the role gate and the cohort gate are different doors. Role governs what a caller may do
        // to ITS OWN session; cohort governs what it may do to everyone else's, and an unjoined
        // caller has no seat to do anything with.
        Set.toList (Affordances.cohortTools Authority.Anonymous)
        |> Expect.equal "an unjoined caller holds cohort status and nothing else" [ Affordances.CohortTool.GetStatus ]
      }

      test "run_tests is refused for an Observer — discovering tests is reading, running them is not" {
        // The sharp edge the `ToolRole` doc names. `list_tests` READS the
        // discovered test list and is in Observer's surface; `run_tests`
        // EXECUTES the project's test binary, which is user code running as
        // the daemon's OS user — the same hazard as send_fsharp_code reached
        // by a different door.
        Affordances.ToolRole.observerTools
        |> Set.contains Affordances.ToolName.ListTests
        |> Expect.isTrue "list_tests only reads the discovered test list, so an Observer keeps it"
        Affordances.checkAuthorityAllowed observer Affordances.ToolName.RunTests
        |> Expect.equal
          "run_tests executes user code, so an Observer does not get it"
          (Error(Affordances.AuthorityRefusal.RoleForbids(Affordances.ToolName.RunTests, Affordances.ToolRole.Working)))
      }

      test "an Observer's grant is a POSITIVE list — it is never 'everything except eval'" {
        // The property that makes the narrow grant mean anything. A deny-list
        // would have to name every dangerous tool by hand; the tools nobody
        // thought of would leak through. Asserted structurally: Observer's
        // surface is a strict, small subset, and the hazardous families are
        // entirely outside it.
        let observerSet = Affordances.ToolRole.toolsOf Affordances.ToolRole.Observer
        Set.isSubset observerSet (Set.ofList Affordances.ToolName.all)
        |> Expect.isTrue "an Observer's grant is a subset of the registered tools"
        Set.isSubset observerSet (Affordances.ToolRole.toolsOf Affordances.ToolRole.Working)
        |> Expect.isTrue "and a strict subset of the working member's"
        for tool in Affordances.ToolRole.conductorOnlyTools do
          Set.contains tool observerSet
          |> Expect.isFalse (sprintf "an Observer does not hold conductor-only %s" (Affordances.ToolName.toString tool))
      }
    ]

    // ── …and in the ACCEPTING direction. A gate that always refuses passes
    //    the tests above and is not a gate. ───────────────────────────────

    testList "the same gate still admits" [
      test "an Observer MAY call the read-only tools it is meant for" {
        let reads =
          [ Affordances.ToolName.GetCohortStatus
            Affordances.ToolName.JoinCohort
            Affordances.ToolName.GetDaemonStatus
            Affordances.ToolName.GetSessionStatus
            Affordances.ToolName.ListSessions
            Affordances.ToolName.SwitchSession
            Affordances.ToolName.CheckFsharpCode
            Affordances.ToolName.Diagnose
            Affordances.ToolName.ListTests ]
        for tool in reads do
          Affordances.checkAuthorityAllowed observer tool
          |> Expect.equal (sprintf "an Observer may call %s" (Affordances.ToolName.toString tool)) (Ok ())
      }

      test "an unjoined caller can still join and still read status — or nobody could ever form a cohort" {
        [ Affordances.ToolName.JoinCohort; Affordances.ToolName.GetCohortStatus ]
        |> List.iter (fun tool ->
          Affordances.checkAuthorityAllowed Authority.Anonymous tool
          |> Expect.equal
            (sprintf "an unjoined caller must stay able to call %s" (Affordances.ToolName.toString tool))
            (Ok ()))
      }

      test "a Verifier MAY run tests — that is the whole reason the role exists" {
        [ Affordances.ToolName.RunTests
          Affordances.ToolName.TargetedVerify
          Affordances.ToolName.AcquireTestSuiteLease ]
        |> List.iter (fun tool ->
          Affordances.checkAuthorityAllowed verifier tool
          |> Expect.equal (sprintf "a Verifier may call %s" (Affordances.ToolName.toString tool)) (Ok ()))
      }

      test "but a Verifier still may NOT evaluate — it reads the code, it does not inject its own" {
        for tool in theThree do
          Affordances.checkAuthorityAllowed verifier tool
          |> Expect.equal
            (sprintf "a Verifier may not call %s" (Affordances.ToolName.toString tool))
            (Error(Affordances.AuthorityRefusal.RoleForbids(tool, Affordances.ToolRole.Working)))
      }

      test "an Implementer MAY evaluate, and MAY admin — so the gate is not simply 'refuse everything'" {
        Affordances.checkAuthorityAllowed implementer Affordances.ToolName.SendFsharpCode
        |> Expect.equal "an Implementer may evaluate" (Ok ())
        Affordances.checkAuthorityAllowed implementer Affordances.ToolName.RunApp
        |> Expect.equal "an Implementer may run the app" (Ok ())
      }

      test "the Conductor may call EVERY registered tool" {
        for tool in Affordances.ToolName.all do
          Affordances.checkAuthorityAllowed conductor tool
          |> Expect.equal
            (sprintf "the conductor may call %s" (Affordances.ToolName.toString tool))
            (Ok ())
      }

      test "an Implementer is refused the conductor-only families" {
        // The reason `ToolRole.Conductor` is a separate case rather than the
        // conductor being folded into `Working`.
        //
        // The refusal is `NotInGrant(tool, Working)`, not `RoleForbids(tool, Conductor)`: these
        // tools are outside even the CONDUCTOR role's own set — `checkAuthorityAllowed` admits a
        // tool when the ROLE ITSELF holds it, and `Conductor` deliberately does not, so a caller
        // reaches them only through the separate cohort gate that demands the conductor seat. So
        // "your grant does not name this tool" is the accurate reason, and the case below shows
        // what it means: even the conductor is refused here.
        for tool in Affordances.ToolRole.conductorOnlyTools do
          Affordances.checkAuthorityAllowed implementer tool
          |> Expect.equal
            (sprintf "an Implementer may not call conductor-only %s" (Affordances.ToolName.toString tool))
            (Error(Affordances.AuthorityRefusal.NotInGrant(tool, Affordances.ToolRole.Working)))
      }
    ]

    // ── The refusal is a NAMED DU carrying the rule and the next action ──

    testList "AuthorityRefusal" [
      test "describe names the tool and the role it would have needed" {
        Affordances.AuthorityRefusal.describe
          (Affordances.AuthorityRefusal.NotInGrant(Affordances.ToolName.SendFsharpCode, Affordances.ToolRole.Working))
        |> Expect.stringContains "the refusal names the tool" "send_fsharp_code"
        Affordances.AuthorityRefusal.describe
          (Affordances.AuthorityRefusal.RoleForbids(Affordances.ToolName.TidyWorkspace, Affordances.ToolRole.Conductor))
        |> Expect.stringContains "and the role required" "Conductor"
      }

      test "every refusal carries a next action, and it is not empty" {
        let refusals =
          [ Affordances.AuthorityRefusal.RoleForbids(Affordances.ToolName.RunApp, Affordances.ToolRole.Working)
            Affordances.AuthorityRefusal.RoleForbids(Affordances.ToolName.RunApp, Affordances.ToolRole.Conductor)
            Affordances.AuthorityRefusal.NotInGrant(Affordances.ToolName.RunTests, Affordances.ToolRole.Observer)
            Affordances.AuthorityRefusal.NotInGrant(Affordances.ToolName.RunTests, Affordances.ToolRole.Verifier)
            Affordances.AuthorityRefusal.NotInGrant(Affordances.ToolName.RunTests, Affordances.ToolRole.Working)
            Affordances.AuthorityRefusal.NotInGrant(Affordances.ToolName.RunTests, Affordances.ToolRole.Conductor)
            Affordances.AuthorityRefusal.CapabilityRequired "not_a_real_tool" ]
        for refusal in refusals do
          let action = Affordances.AuthorityRefusal.nextAction refusal
          System.String.IsNullOrWhiteSpace action
          |> Expect.isFalse (sprintf "%A must name a next action" refusal)
          let described = Affordances.AuthorityRefusal.describe refusal
          System.String.IsNullOrWhiteSpace described
          |> Expect.isFalse (sprintf "%A must describe itself" refusal)
      }

      test "the gate never refuses with an unnamed refusal — the type forbids it" {
        // There is no untyped-string result anywhere on this path: the gate's error type is
        // a DU, so the ratchet that counts that shape DOWN cannot find one here.
        //
        // `IsUnion` is the check that matters. `IsClass` is NOT a discriminator and asserting
        // it is a bug: at the IL level an F# union IS a class, so `IsClass` is true for
        // `AuthorityRefusal` exactly as it is for a record or a DU. Asserting "a union is not
        // a class" therefore fails against correct code and means nothing about case names.
        let errorType = typeof<Affordances.AuthorityRefusal>
        errorType.IsEnum
        |> Expect.isFalse "and a DU is not an enum"
        FSharp.Reflection.FSharpType.IsUnion(errorType, System.Reflection.BindingFlags.Public ||| System.Reflection.BindingFlags.NonPublic)
        |> Expect.isTrue "AuthorityRefusal is a union, so every refusal has a case name"
        FSharp.Reflection.FSharpType.GetUnionCases(errorType, System.Reflection.BindingFlags.Public ||| System.Reflection.BindingFlags.NonPublic)
        |> Array.forall (fun c -> not (System.String.IsNullOrWhiteSpace c.Name))
        |> Expect.isTrue "and no case is anonymous"
      }
    ]

    // ── Fail closed on an undeclared name ────────────────────────────────

    testList "fail closed" [
      test "ToolName.tryParse returns None for a name nobody registered" {
        Affordances.ToolName.tryParse "definitely_not_a_registered_tool"
        |> Expect.equal "an undeclared name is not a ToolName" None
      }

      test "every name declared in ToolName round-trips through tryParse" {
        for tool in Affordances.ToolName.all do
          let name = Affordances.ToolName.toString tool
          Affordances.ToolName.tryParse name
          |> Expect.equal (sprintf "%s round-trips" name) (Some tool)
      }

      test "an undeclared name is ALSO refused by the session-state gate, so it never reaches a tool body" {
        // The authority gate refuses it too, but the two are independent: a
        // caller cannot get an undeclared tool through by having a role.
        Affordances.checkToolCallAllowed SessionState.Ready "definitely_not_a_registered_tool"
        |> Expect.isError "checkToolCallAllowed fails closed on an undeclared tool"
      }
    ]

    // ── The new gate must not WIDEN the old cohort gate ──────────────────

    testList "the cohort gate is a subset, never a widening" [
      test "every tool checkAuthorityAllowed admits, the cohort gate also admitted" {
        // The intersection property. If the whole-surface gate were ever to
        // admit something the ten-verb gate refused, the cohort verbs would
        // have quietly got weaker when this gate was introduced.
        let authorities =
          [ Authority.Anonymous
            observer
            verifier
            implementer
            conductor
            Authority.Member(bob, JoinableRole.Observer)
            Authority.Member(bob, JoinableRole.Verifier)
            Authority.Member(bob, JoinableRole.Implementer)
            Authority.Conductor bob ]
        for authority in authorities do
          for tool in Affordances.ToolName.all do
            match Affordances.ToolName.tryCohortTool tool with
            | None -> ()
            | Some cohortTool ->
              match Affordances.checkAuthorityAllowed authority tool with
              | Ok () ->
                Affordances.checkCohortToolAllowed authority cohortTool
                |> Expect.isTrue
                  (sprintf
                    "admitting %s for %A must not widen past the cohort gate's verdict"
                    (Affordances.ToolName.toString tool)
                    authority)
              | Error _ -> ()
      }

      test "an Observer is still refused every cohort verb but status and join" {
        for tool in Affordances.ToolName.cohortTools do
          match Affordances.ToolName.tryCohortTool tool with
          | None -> failtestf "%s is in the cohort list but has no CohortTool" (Affordances.ToolName.toString tool)
          | Some cohortTool ->
            let cohortAllows = Affordances.checkCohortToolAllowed observer cohortTool
            let authorityAllows = Result.isOk (Affordances.checkAuthorityAllowed observer tool)
            authorityAllows
            |> Expect.equal
              (sprintf "the two gates agree on %s for an Observer" (Affordances.ToolName.toString tool))
              cohortAllows
      }
    ]

    // ── ToolName agrees with every other name list in the repo ───────────

    testList "ToolName is the registered tool set" [
      test "toToolName's image equals the gating domain's keys" {
        // `gatingDomain` (per-SESSION-STATE classification) and `ToolName`
        // (the DU the authority gate is keyed on) are two views of ONE table.
        // A tool added to one and not the other would be a tool that bypasses
        // one of the two gates.
        Affordances.ToolName.allToolNames
        |> Set.ofList
        |> Expect.equal "ToolName.allToolNames ≡ declaredGateTools" (Set.ofList Affordances.declaredGateTools)
      }

      test "toToolName's image equals the union of Capability's tool classes" {
        // And the third name list: `Capability.ToolClass.toolsOf`, the
        // per-TOKEN-CLASS classification. All three hold to one set.
        let classed =
          Capability.ToolClass.all
          |> List.collect Capability.ToolClass.toolsOf
          |> Set.ofList
        Affordances.ToolName.allToolNames
        |> Set.ofList
        |> Expect.equal "ToolName.allToolNames ≡ every Capability tool class" classed
      }

      test "toToolName is injective — no two DU cases share a name" {
        Affordances.ToolName.all
        |> List.map Affordances.ToolName.toString
        |> List.length
        |> Expect.equal "every DU case has a distinct name" (Set.ofList Affordances.ToolName.allToolNames |> Set.count)
      }

      test "every DU case is reachable from all — so the gate cannot have a silent hole" {
        for tool in Affordances.ToolName.all do
          Set.contains tool (Affordances.ToolRole.toolsOf Affordances.ToolRole.Conductor)
          |> Expect.isTrue (sprintf "%s is in the conductor's surface" (Affordances.ToolName.toString tool))
      }
    ]

    // ── The role ladder is monotone ──────────────────────────────────────

    testList "ToolRole ordering" [
      test "Observer ⊆ Verifier ⊆ Working ⊆ Conductor, by tool set" {
        let pairs =
          [ Affordances.ToolRole.Observer, Affordances.ToolRole.Verifier
            Affordances.ToolRole.Verifier, Affordances.ToolRole.Working
            Affordances.ToolRole.Working, Affordances.ToolRole.Conductor ]
        for (narrower, wider) in pairs do
          Affordances.ToolRole.isNarrowerOrEqual narrower wider
          |> Expect.isTrue
            (sprintf "%s is narrower than %s" (Affordances.ToolRole.toToken narrower) (Affordances.ToolRole.toToken wider))
      }

      test "and strictly so — no two adjacent roles are the same set" {
        let distinct =
          Affordances.ToolRole.all
          |> List.map (fun r -> Affordances.ToolRole.toolsOf r |> Set.count)
          |> List.distinct
        distinct
        |> List.length
        |> Expect.equal "each role is a distinct size, so no role duplicates another" 4
      }

      test "every authority maps to a role, and the conductor is its own role" {
        // `Anonymous` is `Working`, deliberately and against the old expectation. It used to be
        // `Observer`, which locked every unjoined caller out of its own session the moment anyone
        // formed a cohort. A caller with no seat has no ROLE to narrow it; what stops it reaching
        // other members' work is that it is still `Anonymous` in `Cohort.Authority`, so every
        // cohort verb refuses it. The case below and the one in "Observer cannot run..." pin both.
        Affordances.ToolRole.ofAuthority Authority.Anonymous
        |> Expect.equal "an unjoined caller has no role to narrow it, so it maps to Working" Affordances.ToolRole.Working
        Affordances.ToolRole.ofAuthority conductor
        |> Expect.equal "the conductor is the Conductor role, not Working" Affordances.ToolRole.Conductor
      }
    ]
  ]