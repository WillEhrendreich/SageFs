/// Where a member token may route: the pure decision (SageFs.Core/Capability.fs).
///
/// A grant names ONE of three things, never a bool and never an option: `Unbound` (the
/// conductor's authority, which no mint can hand out), `BoundToSession` or `BoundToCheckout`.
/// `Route.admit` is the one decision every call from a bound token passes through, so a tool
/// added tomorrow gets an answer from it or is refused (`UnclassifiedRouting`), never skipped.
///
/// These cover the happy path, each refusal, every routing parameter, the unbound case, the
/// partial order on bindings, a safety property over every tool and argument, a negative control
/// that a bound caller still reaches what it is bound to, and mutants of each decision.
module SageFs.Tests.RouteBindingTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.Capability
open SageFs.Tests.SharedGenerators
open MutationTestingFramework

// ── Fixtures ────────────────────────────────────────────────────────────

let sessionA = "a1a1a1a1"
let sessionB = "b2b2b2b2"
let sessionC = "c3c3c3c3"
let dirOfA = "/work/a"
let dirOfB = "/work/b"
let dirOfC = "/work/c"

let rootOrFail (raw: string) : CheckoutRoot =
  CheckoutRoot.tryParse raw |> Expect.wantOk (sprintf "'%s' is a checkout root" raw)

let checkoutA = RouteBinding.BoundToCheckout(rootOrFail dirOfA)
let checkoutB = RouteBinding.BoundToCheckout(rootOrFail dirOfB)
let sessionBoundA = RouteBinding.BoundToSession sessionA

/// The whole-segment prefix rule, standing in for the production one, which also honours a worktree boundary.
let within (root: string) (directory: string) : bool =
  directory = root || directory.StartsWith(root + "/", StringComparison.Ordinal)

let dirOfSession (sessionId: string) : string option =
  match sessionId with
  | id when id = sessionA -> Some dirOfA
  | id when id = sessionB -> Some dirOfB
  | id when id = sessionC -> Some dirOfC
  | _ -> None

/// What the registry says, for a call naming `named` and (when the token is bound to a session) that session.
let factsFor (binding: RouteBinding) (named: string option) : RouteFacts =
  { NamedSessionDirectory = named |> Option.bind dirOfSession
    BoundSessionDirectory =
      match binding with
      | RouteBinding.BoundToSession id -> dirOfSession id
      | RouteBinding.Unbound
      | RouteBinding.BoundToCheckout _ -> None
    Within = within }

let call (tool: string) (sessionId: string option) (workingDirectory: string option) : RouteCall =
  { Tool = tool; SessionId = sessionId; WorkingDirectory = workingDirectory }

let admit (binding: RouteBinding) (c: RouteCall) : Result<unit, RouteRefusal> =
  Route.admit binding c (factsFor binding c.SessionId)

let onSession = "check_fsharp_code"
let listing = "list_sessions"
let creating = "create_project_session"
let cohort = "join_cohort"
let reading = "get_available_projects"
let daemonWide = "get_daemon_status"

let isRefusedWith (describe: string) (matches: RouteRefusal -> bool) (verdict: Result<unit, RouteRefusal>) =
  match verdict with
  | Ok() -> failtestf "%s: was admitted" describe
  | Error refusal -> matches refusal |> Expect.isTrue (sprintf "%s: refused for the wrong reason: %A" describe refusal)

// ── Parsing a binding: parse, do not validate ───────────────────────────

[<Tests>]
let parsingTests =
  testList "Route binding: what a mint may name" [

    testCase "WHY - a checkout root is an absolute, canonical directory, so one directory has one spelling" <| fun () ->
      CheckoutRoot.tryParse "/work/a/" |> Result.map CheckoutRoot.value |> Expect.equal "trailing separator" (Ok "/work/a")
      CheckoutRoot.tryParse "/work/x/../a" |> Result.map CheckoutRoot.value |> Expect.equal "a detour is resolved" (Ok "/work/a")
      (CheckoutRoot.tryParse "/work/a" = CheckoutRoot.tryParse "/work/a/./") |> Expect.isTrue "the same directory parses to the same root"

    testCase "WHY - a relative or blank directory is not a checkout root, because it names nothing" <| fun () ->
      CheckoutRoot.tryParse "src/Foo" |> Expect.equal "relative" (Error(RootRefusal.NotAbsolute "src/Foo"))
      CheckoutRoot.tryParse "" |> Expect.equal "empty" (Error RootRefusal.Blank)
      CheckoutRoot.tryParse "   " |> Expect.equal "blank" (Error RootRefusal.Blank)

    testCase "WHY - a request names a session or a working directory, and the result is never Unbound" <| fun () ->
      RouteBinding.ofRequest (Some sessionA) None |> Expect.equal "a session" (Ok(RouteBinding.BoundToSession sessionA))
      RouteBinding.ofRequest None (Some dirOfA) |> Expect.equal "a checkout" (Ok checkoutA)
      RouteBinding.ofRequest (Some(" " + sessionA + " ")) None |> Expect.equal "the id is trimmed" (Ok(RouteBinding.BoundToSession sessionA))

    testCase "WHY - naming neither is refused, and so is naming both: a grant has ONE binding" <| fun () ->
      RouteBinding.ofRequest None None |> Expect.equal "nothing named" (Error BindRefusal.NothingNamed)
      RouteBinding.ofRequest (Some "") (Some "  ") |> Expect.equal "blank is nothing" (Error BindRefusal.NothingNamed)
      RouteBinding.ofRequest (Some sessionA) (Some dirOfA) |> Expect.equal "both named" (Error BindRefusal.BothNamed)

    testCase "WHY - a relative working directory is refused with the text it was given" <| fun () ->
      RouteBinding.ofRequest None (Some "src/Foo") |> Expect.equal "relative" (Error(BindRefusal.NotAbsolute "src/Foo"))
  ]

// ── The order on bindings ───────────────────────────────────────────────

type RouteGenerators =
  static member Binding() : Arbitrary<RouteBinding> =
    Gen.elements
      [ RouteBinding.Unbound
        RouteBinding.BoundToSession sessionA
        RouteBinding.BoundToSession sessionB
        checkoutA
        checkoutB
        RouteBinding.BoundToCheckout(rootOrFail dirOfC)
        RouteBinding.BoundToCheckout(rootOrFail "/work") ]
    |> Arb.fromGen

let routeConfig = { propConfig with arbitrary = [ typeof<RouteGenerators> ] }

[<Tests>]
let orderTests =
  testList "Route binding: the order" [

    testCase "WHY - Unbound is the top: every binding is within it, and it is within only itself" <| fun () ->
      for bound in [ sessionBoundA; checkoutA ] do
        RouteBinding.isNarrowerOrEqual bound RouteBinding.Unbound |> Expect.isTrue (sprintf "%A is narrower than Unbound" bound)
        RouteBinding.isNarrowerOrEqual RouteBinding.Unbound bound |> Expect.isFalse (sprintf "Unbound is not within %A" bound)
      RouteBinding.isNarrowerOrEqual RouteBinding.Unbound RouteBinding.Unbound |> Expect.isTrue "Unbound is within itself"

    testCase "WHY - two different bindings are unrelated, so a bound minter can only hand out its own" <| fun () ->
      RouteBinding.isNarrowerOrEqual (RouteBinding.BoundToSession sessionB) sessionBoundA |> Expect.isFalse "another session"
      RouteBinding.isNarrowerOrEqual checkoutB checkoutA |> Expect.isFalse "another checkout"
      RouteBinding.isNarrowerOrEqual sessionBoundA checkoutA |> Expect.isFalse "a session is not provably inside a checkout from the grant alone"
      RouteBinding.isNarrowerOrEqual checkoutA sessionBoundA |> Expect.isFalse "a checkout is wider than a session"
      RouteBinding.isNarrowerOrEqual sessionBoundA sessionBoundA |> Expect.isTrue "a binding is within itself"
      RouteBinding.isNarrowerOrEqual checkoutA checkoutA |> Expect.isTrue "a checkout is within itself"

    testPropertyWithConfig routeConfig "the order on bindings is a partial order" <| fun (a: RouteBinding) (b: RouteBinding) (c: RouteBinding) ->
      let reflexive = RouteBinding.isNarrowerOrEqual a a
      let antisymmetric = not (RouteBinding.isNarrowerOrEqual a b && RouteBinding.isNarrowerOrEqual b a) || a = b
      let transitive = not (RouteBinding.isNarrowerOrEqual a b && RouteBinding.isNarrowerOrEqual b c) || RouteBinding.isNarrowerOrEqual a c
      reflexive && antisymmetric && transitive
  ]

// ── What a mint may hand out ────────────────────────────────────────────

let epoch = DateTime(2026, 10, 4, 9, 0, 0, DateTimeKind.Utc)

let grantFor (route: RouteBinding) : Grant =
  { Preset = RolePreset.Analysis; Scope = ScopePrefix.repoRoot; Route = route; NotAfter = epoch + TestTimeouts.tokenRun }

let conductorMinter = { Authority = Cohort.Authority.Conductor(MemberTable.MemberId.Minted "conductor"); Grant = Grant.conductorAuthority }

let mintAs (minter: Minter) (requested: Grant) =
  let hash = TokenHash.ofToken (Token.ofEntropy (Array.create Token.entropyBytes 7uy))
  decide epoch CapabilityState.empty (CapabilityCommand.Mint(minter, requested, hash))

[<Tests>]
let mintTests =
  testList "Route binding: minting" [

    testCase "WHY - the conductor's own authority is the one Unbound grant" <| fun () ->
      Grant.conductorAuthority.Route |> Expect.equal "unbound by design" RouteBinding.Unbound

    testCase "WHY - the conductor mints a token bound to a session or a checkout, and the record keeps the binding" <| fun () ->
      for route in [ sessionBoundA; checkoutA ] do
        match mintAs conductorMinter (grantFor route) with
        | Ok(state, _) -> (state.Records |> Map.toList |> List.exactlyOne |> snd).Grant.Route |> Expect.equal "bound as asked" route
        | Error refusal -> failtestf "minting %A was refused: %A" route refusal

    testCase "WHY - nobody can mint an Unbound token, the conductor included: a grant must name where it routes" <| fun () ->
      mintAs conductorMinter (grantFor RouteBinding.Unbound)
      |> Expect.equal "refused by name" (Error(CapabilityRefusal.Mint MintRefusal.UnboundNotMintable))

    testCase "WHY - a bound minter mints only its own binding: another session or checkout is a widening, named, not clamped" <| fun () ->
      let minter = { conductorMinter with Grant = grantFor sessionBoundA }
      match mintAs minter (grantFor (RouteBinding.BoundToSession sessionB)) with
      | Error(CapabilityRefusal.Mint(MintRefusal.WouldWiden widenings)) ->
        widenings
        |> List.contains (Widening.Route(RouteBinding.BoundToSession sessionB, sessionBoundA))
        |> Expect.isTrue "the route widening is named"
      | other -> failtestf "expected WouldWiden, got %A" other
      mintAs minter (grantFor sessionBoundA) |> Result.isOk |> Expect.isTrue "its own binding is allowed"

    testCase "WHY - an Unbound request from a bound minter is a refusal, not a widening to Unbound" <| fun () ->
      let minter = { conductorMinter with Grant = grantFor checkoutA }
      mintAs minter (grantFor RouteBinding.Unbound)
      |> Expect.equal "refused" (Error(CapabilityRefusal.Mint MintRefusal.UnboundNotMintable))

    testCase "WHY - a grant is wider than another exactly when its route is wider, among other things" <| fun () ->
      let widenings = Grant.wideningsOf (grantFor checkoutA) (grantFor checkoutB)
      widenings |> List.exists (function Widening.Route _ -> true | _ -> false) |> Expect.isTrue "a different checkout is a route widening"
      Grant.wideningsOf (grantFor checkoutA) (grantFor checkoutA) |> Expect.equal "equal is not wider" []
      Grant.isNarrowerOrEqual (grantFor checkoutA) Grant.conductorAuthority |> Expect.isTrue "bound is narrower than the conductor"
  ]

// ── The tool table: every tool has an answer ────────────────────────────

[<Tests>]
let kindTests =
  testList "Route binding: every tool says how it routes" [

    testCase "WHY - every tool a role can reach has a routing kind, and no tool outside the roles has one" <| fun () ->
      let classified = ToolClass.all |> List.collect ToolClass.toolsOf |> Set.ofList
      let withKind = RouteKind.assignments |> List.map fst |> Set.ofList
      Set.difference classified withKind |> Set.toList |> Expect.equal "a classified tool with no routing kind is skipped by the gate" []
      Set.difference withKind classified |> Set.toList |> Expect.equal "a routing kind for a tool nobody classified" []
      (RouteKind.assignments |> List.map fst |> List.distinct |> List.length) |> Expect.equal "one kind per tool" RouteKind.assignments.Length

    testCase "WHY - a tool the table does not know has no kind, so a bound token is refused it" <| fun () ->
      RouteKind.ofTool "a_tool_added_tomorrow" |> Expect.equal "no kind" None
      admit checkoutA (call "a_tool_added_tomorrow" None None)
      |> isRefusedWith "an unclassified tool" (function RouteRefusal.UnclassifiedRouting "a_tool_added_tomorrow" -> true | _ -> false)

    testCase "WHY - the kinds are a closed list with one distinct token each" <| fun () ->
      let cases = Reflection.FSharpType.GetUnionCases typeof<RouteKind>
      RouteKind.all.Length |> Expect.equal "RouteKind.all names every union case" cases.Length
      (RouteKind.all |> List.map RouteKind.toToken |> List.distinct |> List.length) |> Expect.equal "tokens are distinct" cases.Length

    testCase "WHY - the cohort verbs route by repository, session creation creates, the rest of the table is as documented" <| fun () ->
      for tool in ToolClass.toolsOf ToolClass.CohortRead @ ToolClass.toolsOf ToolClass.CohortMembership @ ToolClass.toolsOf ToolClass.CohortWork @ ToolClass.toolsOf ToolClass.CohortAdmin do
        RouteKind.ofTool tool |> Expect.equal (sprintf "%s picks a cohort by directory" tool) (Some RouteKind.InCohort)
      for tool in [ "create_project_session"; "create_solution_session"; "create_bare_session" ] do
        RouteKind.ofTool tool |> Expect.equal (sprintf "%s creates" tool) (Some RouteKind.CreatesSession)
      RouteKind.ofTool "list_sessions" |> Expect.equal "listing" (Some RouteKind.ListsSessions)
      RouteKind.ofTool "get_available_projects" |> Expect.equal "reads a directory" (Some RouteKind.ReadsDirectory)
      RouteKind.ofTool "get_daemon_status" |> Expect.equal "daemon-wide" (Some RouteKind.DaemonWide)

    testCase "WHY - a tool whose availability depends on a session's state acts on a session, unless it creates one" <| fun () ->
      for tool in Affordances.declaredGateTools do
        match Affordances.toolGate tool, RouteKind.ofTool tool with
        | Some Affordances.ToolGate.StateGated, Some kind ->
          (kind = RouteKind.OnSession || kind = RouteKind.CreatesSession)
          |> Expect.isTrue (sprintf "%s depends on a session's state yet is %A" tool kind)
        | _ -> ()
  ]

// ── The decision ────────────────────────────────────────────────────────

[<Tests>]
let admitTests =
  testList "Route.admit" [

    testList "Unbound: the conductor and an untokened connection route anywhere, as before" [
      testCase "every routing parameter is admitted, on every kind of tool" <| fun () ->
        for tool in [ onSession; listing; creating; cohort; reading; daemonWide ] do
          admit RouteBinding.Unbound (call tool (Some sessionB) (Some dirOfB)) |> Expect.equal (sprintf "%s names B" tool) (Ok())
          admit RouteBinding.Unbound (call tool None None) |> Expect.equal (sprintf "%s names nothing" tool) (Ok())
      testCase "a tool nobody classified is admitted too: the table binds a token, not the conductor" <| fun () ->
        admit RouteBinding.Unbound (call "a_tool_added_tomorrow" (Some sessionB) None) |> Expect.equal "unbound" (Ok())
    ]

    testList "bound to a session" [
      testCase "its own session is admitted, by id and by a directory inside the session's own" <| fun () ->
        admit sessionBoundA (call onSession (Some sessionA) None) |> Expect.equal "by id" (Ok())
        admit sessionBoundA (call onSession None (Some dirOfA)) |> Expect.equal "by its directory" (Ok())
        admit sessionBoundA (call onSession None (Some(dirOfA + "/tests"))) |> Expect.equal "from inside it" (Ok())
        admit sessionBoundA (call onSession (Some sessionA) (Some dirOfA)) |> Expect.equal "both, consistent" (Ok())
      testCase "naming no session and no directory is admitted: the call acts on the bound session, which the confinement guarantees" <| fun () ->
        admit sessionBoundA (call onSession None None) |> Expect.equal "implicit" (Ok())
      testCase "another session by id is refused, and the refusal names the session the token IS bound to" <| fun () ->
        let verdict = admit sessionBoundA (call onSession (Some sessionB) None)
        verdict |> isRefusedWith "B by id" (function RouteRefusal.SessionOutsideBinding(requested, binding) -> requested = sessionB && binding = sessionBoundA | _ -> false)
        match verdict with
        | Error refusal -> RouteRefusal.nextAction refusal |> Expect.stringContains "the next action says which session to use" sessionA
        | Ok() -> ()
      testCase "another session by directory is refused" <| fun () ->
        admit sessionBoundA (call onSession None (Some dirOfB))
        |> isRefusedWith "B by directory" (function RouteRefusal.DirectoryOutsideBinding(requested, _) -> requested = dirOfB | _ -> false)
      testCase "a directory that only STARTS the same as its own is another directory" <| fun () ->
        admit sessionBoundA (call onSession None (Some(dirOfA + "-other")))
        |> isRefusedWith "sibling with a shared prefix" (function RouteRefusal.DirectoryOutsideBinding _ -> true | _ -> false)
      testCase "a detour out of its own directory is refused, because the directory is resolved first" <| fun () ->
        admit sessionBoundA (call onSession None (Some(dirOfA + "/../b")))
        |> isRefusedWith "dot-dot out" (function RouteRefusal.DirectoryOutsideBinding _ -> true | _ -> false)
      testCase "a detour that stays inside is admitted" <| fun () ->
        admit sessionBoundA (call onSession None (Some(dirOfA + "/src/../tests"))) |> Expect.equal "stays inside" (Ok())
      testCase "a relative directory names nothing it can be shown to be inside, so it is refused" <| fun () ->
        admit sessionBoundA (call onSession None (Some "tests"))
        |> isRefusedWith "relative" (function RouteRefusal.DirectoryOutsideBinding _ -> true | _ -> false)
      testCase "when the daemon no longer serves the bound session, a directory cannot be checked and is refused, and the refusal says so" <| fun () ->
        let binding = RouteBinding.BoundToSession "deadbeef"
        admit binding (call onSession None (Some dirOfA))
        |> isRefusedWith "bound session is gone" (function RouteRefusal.BoundSessionNotServed "deadbeef" -> true | _ -> false)
        admit binding (call onSession (Some "deadbeef") None) |> Expect.equal "naming it by id is still its own id" (Ok())
      testCase "it cannot create a session: creating is routing to a session it is not bound to" <| fun () ->
        admit sessionBoundA (call creating None (Some dirOfA))
        |> isRefusedWith "create" (function RouteRefusal.CannotCreateSessions _ -> true | _ -> false)
      testCase "a cohort verb or a directory read must name its directory, and it must be inside the session's" <| fun () ->
        for tool in [ cohort; reading ] do
          admit sessionBoundA (call tool None None) |> isRefusedWith (sprintf "%s names no directory" tool) (function RouteRefusal.DirectoryRequired(t, _) -> t = tool | _ -> false)
          admit sessionBoundA (call tool None (Some dirOfA)) |> Expect.equal (sprintf "%s in its own directory" tool) (Ok())
          admit sessionBoundA (call tool None (Some dirOfB)) |> isRefusedWith (sprintf "%s in B's directory" tool) (function RouteRefusal.DirectoryOutsideBinding _ -> true | _ -> false)
      testCase "listing sessions and daemon status name nothing, so they are admitted (the confinement filters what they show)" <| fun () ->
        admit sessionBoundA (call listing None None) |> Expect.equal "listing" (Ok())
        admit sessionBoundA (call daemonWide None None) |> Expect.equal "daemon" (Ok())
    ]

    testList "bound to a checkout" [
      testCase "a session inside the checkout is admitted by id, and so is the checkout's directory" <| fun () ->
        admit checkoutA (call onSession (Some sessionA) None) |> Expect.equal "session in A" (Ok())
        admit checkoutA (call onSession None (Some dirOfA)) |> Expect.equal "A's directory" (Ok())
        admit checkoutA (call onSession None (Some(dirOfA + "/src"))) |> Expect.equal "inside A" (Ok())
      testCase "a session outside the checkout is refused by id, by directory, and when the id is not served at all" <| fun () ->
        admit checkoutA (call onSession (Some sessionB) None) |> isRefusedWith "B by id" (function RouteRefusal.SessionOutsideBinding _ -> true | _ -> false)
        admit checkoutA (call onSession None (Some dirOfB)) |> isRefusedWith "B by directory" (function RouteRefusal.DirectoryOutsideBinding _ -> true | _ -> false)
        admit checkoutA (call onSession (Some "00000000") None)
        |> isRefusedWith "an id nobody serves is reported as outside, not as missing" (function RouteRefusal.SessionOutsideBinding _ -> true | _ -> false)
      testCase "a parent of the checkout is outside it" <| fun () ->
        admit checkoutA (call onSession None (Some "/work")) |> isRefusedWith "parent" (function RouteRefusal.DirectoryOutsideBinding _ -> true | _ -> false)
      testCase "it creates sessions inside its checkout and nowhere else" <| fun () ->
        admit checkoutA (call creating None (Some dirOfA)) |> Expect.equal "inside" (Ok())
        admit checkoutA (call creating None (Some(dirOfA + "/sub"))) |> Expect.equal "deeper inside" (Ok())
        admit checkoutA (call creating None (Some dirOfB)) |> isRefusedWith "elsewhere" (function RouteRefusal.DirectoryOutsideBinding _ -> true | _ -> false)
        admit checkoutA (call creating None None) |> isRefusedWith "no directory" (function RouteRefusal.DirectoryRequired(t, _) -> t = creating | _ -> false)
      testCase "a cohort verb or a directory read must name a directory inside the checkout" <| fun () ->
        for tool in [ cohort; reading ] do
          admit checkoutA (call tool None None) |> isRefusedWith (sprintf "%s without a directory" tool) (function RouteRefusal.DirectoryRequired _ -> true | _ -> false)
          admit checkoutA (call tool None (Some dirOfA)) |> Expect.equal (sprintf "%s inside" tool) (Ok())
          admit checkoutA (call tool None (Some dirOfB)) |> isRefusedWith (sprintf "%s outside" tool) (function RouteRefusal.DirectoryOutsideBinding _ -> true | _ -> false)
      testCase "a directory a call volunteers is held to the binding on every kind of tool, even one that does not route by it" <| fun () ->
        for tool in [ listing; daemonWide ] do
          admit checkoutA (call tool None (Some dirOfB)) |> isRefusedWith (sprintf "%s naming B's directory" tool) (function RouteRefusal.DirectoryOutsideBinding _ -> true | _ -> false)
          admit checkoutA (call tool None (Some dirOfA)) |> Expect.equal (sprintf "%s naming its own" tool) (Ok())
      testCase "a session id a call volunteers is held to the binding on every kind of tool too" <| fun () ->
        for tool in [ listing; daemonWide; cohort; reading; creating ] do
          admit checkoutA (call tool (Some sessionB) (Some dirOfA)) |> isRefusedWith (sprintf "%s naming session B" tool) (function RouteRefusal.SessionOutsideBinding _ -> true | _ -> false)
    ]

    testList "every refusal carries the rule and the next action" [
      testCase "each case describes what was refused and says what to do, naming what the token IS bound to" <| fun () ->
        let refusals =
          [ RouteRefusal.SessionOutsideBinding(sessionB, sessionBoundA), sessionA
            RouteRefusal.SessionOutsideBinding(sessionB, checkoutA), dirOfA
            RouteRefusal.DirectoryOutsideBinding(dirOfB, sessionBoundA), sessionA
            RouteRefusal.DirectoryOutsideBinding(dirOfB, checkoutA), dirOfA
            RouteRefusal.BoundSessionNotServed sessionA, sessionA
            RouteRefusal.DirectoryRequired(cohort, checkoutA), dirOfA
            RouteRefusal.DirectoryRequired(cohort, sessionBoundA), sessionA
            RouteRefusal.CannotCreateSessions sessionBoundA, sessionA
            RouteRefusal.UnclassifiedRouting "a_tool_added_tomorrow", "a_tool_added_tomorrow" ]
        for (refusal, mustName) in refusals do
          RouteRefusal.describe refusal |> Expect.isNotEmpty (sprintf "%A has a reason" refusal)
          let next = RouteRefusal.nextAction refusal
          next |> Expect.isNotEmpty (sprintf "%A has a next action" refusal)
          next |> Expect.stringContains (sprintf "%A names what the caller should use instead" refusal) mustName
    ]
  ]

// ── The safety property, over every tool and every argument ─────────────

let sessionPool = [ None; Some sessionA; Some sessionB; Some sessionC; Some "00000000" ]

let directoryPool =
  [ None
    Some dirOfA
    Some dirOfB
    Some(dirOfA + "/src")
    Some(dirOfA + "/../b")
    Some(dirOfA + "/src/..")
    Some(dirOfA + "-other")
    Some "/work"
    Some "relative/path"
    Some "/" ]

let allTools = (RouteKind.assignments |> List.map fst) @ [ "a_tool_added_tomorrow" ]

/// The oracle: written from the doctrine, not from the implementation.
let sessionAllowed (binding: RouteBinding) (sessionId: string option) : bool =
  match sessionId, binding with
  | None, _ -> true
  | Some _, RouteBinding.Unbound -> true
  | Some id, RouteBinding.BoundToSession bound -> id = bound
  | Some id, RouteBinding.BoundToCheckout root ->
    match dirOfSession id with
    | Some dir -> within (CheckoutRoot.value root) dir
    | None -> false

let directoryAllowed (binding: RouteBinding) (directory: string option) : bool =
  let resolved (raw: string) = if raw.StartsWith "/" then Some(IO.Path.GetFullPath raw) else None
  match directory, binding with
  | None, _ -> true
  | Some _, RouteBinding.Unbound -> true
  | Some raw, RouteBinding.BoundToCheckout root -> resolved raw |> Option.exists (within (CheckoutRoot.value root))
  | Some raw, RouteBinding.BoundToSession bound ->
    match dirOfSession bound with
    | Some dir -> resolved raw |> Option.exists (within dir)
    | None -> false

[<Tests>]
let propertyTests =
  testList "Route.admit: a bound token is never admitted for what it was not bound to" [

    testPropertyWithConfig routeConfig "ADMITTED implies every session and directory the call names is inside the binding" <| fun (binding: RouteBinding) (toolIndex: PositiveInt) (sessionIndex: PositiveInt) (dirIndex: PositiveInt) ->
      let tool = allTools.[toolIndex.Get % allTools.Length]
      let sessionId = sessionPool.[sessionIndex.Get % sessionPool.Length]
      let directory = directoryPool.[dirIndex.Get % directoryPool.Length]
      match admit binding (call tool sessionId directory) with
      | Ok() when binding = RouteBinding.Unbound -> true
      | Ok() -> sessionAllowed binding sessionId && directoryAllowed binding directory
      | Error _ -> true

    testPropertyWithConfig routeConfig "for any bound token and any session id other than its own, no routing tool admits it" <| fun (binding: RouteBinding) (toolIndex: PositiveInt) (sessionIndex: PositiveInt) ->
      let tool = allTools.[toolIndex.Get % allTools.Length]
      let sessionId = sessionPool.[sessionIndex.Get % sessionPool.Length]
      match binding, sessionId with
      | RouteBinding.Unbound, _
      | _, None -> true
      | _, Some _ when sessionAllowed binding sessionId -> true
      | _, Some _ -> Result.isError (admit binding (call tool sessionId None))

    testPropertyWithConfig routeConfig "NEGATIVE CONTROL: a call on a session tool that names only what the binding allows is admitted" <| fun (binding: RouteBinding) (sessionIndex: PositiveInt) (dirIndex: PositiveInt) ->
      let sessionId = sessionPool.[sessionIndex.Get % sessionPool.Length]
      let directory = directoryPool.[dirIndex.Get % directoryPool.Length]
      match binding with
      | RouteBinding.BoundToSession bound when dirOfSession bound = None -> true
      | _ ->
        match sessionAllowed binding sessionId && directoryAllowed binding directory with
        | true -> admit binding (call onSession sessionId directory) = Ok()
        | false -> true
  ]

// ── Mutants: each breaks exactly one decision ───────────────────────────

type AdmitInput = RouteBinding * RouteCall * RouteFacts
type AdmitFn = AdmitInput -> Result<unit, RouteRefusal>

let admitOf ((binding, c, facts): AdmitInput) : Result<unit, RouteRefusal> = Route.admit binding c facts

let inputFor (binding: RouteBinding) (c: RouteCall) : AdmitInput = binding, c, factsFor binding c.SessionId

let skipsTheSessionCheck : Mutant<AdmitFn> =
  { Name = "admit_skips_the_session_id"
    Description = "a token bound to A must never be admitted for B named by id"
    Apply = fun real (b, c, f) -> real (b, { c with SessionId = None }, f) }

let skipsTheDirectoryCheck : Mutant<AdmitFn> =
  { Name = "admit_skips_the_working_directory"
    Description = "a token bound to A must never be admitted for B named by directory"
    Apply = fun real (b, c, f) -> real (b, { c with WorkingDirectory = None }, f) }

let swapsWithinArguments : Mutant<AdmitFn> =
  { Name = "admit_asks_within_the_wrong_way_round"
    Description = "is the directory inside the root, not the root inside the directory"
    Apply = fun real (b, c, f) -> real (b, c, { f with Within = fun root dir -> f.Within dir root }) }

let dropsDirectoryRequired : Mutant<AdmitFn> =
  { Name = "admit_lets_a_cohort_verb_default_its_directory"
    Description = "a bound token that names no directory acts in the daemon's own cohort, which is outside its binding"
    Apply = fun real (b, c, f) ->
      match RouteKind.ofTool c.Tool, c.WorkingDirectory with
      | Some RouteKind.InCohort, None -> Ok()
      | _ -> real (b, c, f) }

let sessionBoundMayCreate : Mutant<AdmitFn> =
  { Name = "admit_lets_a_session_bound_token_create"
    Description = "creating a session is routing to one the token is not bound to"
    Apply = fun real (b, c, f) ->
      match b, RouteKind.ofTool c.Tool with
      | RouteBinding.BoundToSession _, Some RouteKind.CreatesSession -> Ok()
      | _ -> real (b, c, f) }

let unboundIsRefused : Mutant<AdmitFn> =
  { Name = "admit_refuses_unbound"
    Description = "the conductor and an untokened connection route anywhere"
    Apply = fun real (b, c, f) ->
      match b with
      | RouteBinding.Unbound -> Error(RouteRefusal.UnclassifiedRouting c.Tool)
      | _ -> real (b, c, f) }

let unclassifiedIsAdmitted : Mutant<AdmitFn> =
  { Name = "admit_admits_a_tool_with_no_kind"
    Description = "a tool added tomorrow with no routing kind must be refused for a bound token"
    Apply = fun real (b, c, f) ->
      match RouteKind.ofTool c.Tool with
      | None -> Ok()
      | Some _ -> real (b, c, f) }

let doesNotResolveDetours : Mutant<AdmitFn> =
  { Name = "admit_does_not_resolve_dot_dot"
    Description = "a/../b is b, not inside a"
    Apply = fun real (b, c, f) ->
      let textual (raw: string) = if raw.Contains "/../" then raw.Substring(0, raw.IndexOf "/../") else raw
      real (b, { c with WorkingDirectory = c.WorkingDirectory |> Option.map textual }, f) }

let unknownNamedSessionIsAdmitted : Mutant<AdmitFn> =
  { Name = "admit_admits_a_session_id_the_daemon_does_not_serve"
    Description = "an id with no directory cannot be shown to be inside a checkout"
    Apply = fun real (b, c, f) ->
      match f.NamedSessionDirectory with
      | None -> real (b, { c with SessionId = None }, f)
      | Some _ -> real (b, c, f) }

let isError (result: Result<unit, RouteRefusal>) = Result.isError result
let isOk (result: Result<unit, RouteRefusal>) = Result.isOk result

[<Tests>]
let admitMutationTests =
  testList "Route.admit mutants are caught" [
    detectsOutputMutant skipsTheSessionCheck (inputFor sessionBoundA (call onSession (Some sessionB) None)) admitOf isError
    detectsOutputMutant skipsTheSessionCheck (inputFor checkoutA (call onSession (Some sessionB) None)) admitOf isError
    detectsOutputMutant skipsTheDirectoryCheck (inputFor sessionBoundA (call onSession None (Some dirOfB))) admitOf isError
    detectsOutputMutant skipsTheDirectoryCheck (inputFor checkoutA (call onSession None (Some dirOfB))) admitOf isError
    detectsOutputMutant swapsWithinArguments (inputFor checkoutA (call onSession None (Some "/work"))) admitOf isError
    detectsOutputMutant swapsWithinArguments (inputFor checkoutA (call onSession None (Some(dirOfA + "/src")))) admitOf isOk
    detectsOutputMutant dropsDirectoryRequired (inputFor checkoutA (call cohort None None)) admitOf isError
    detectsOutputMutant dropsDirectoryRequired (inputFor sessionBoundA (call reading None None)) admitOf isError
    detectsOutputMutant sessionBoundMayCreate (inputFor sessionBoundA (call creating None (Some dirOfA))) admitOf isError
    detectsOutputMutant unboundIsRefused (inputFor RouteBinding.Unbound (call onSession (Some sessionB) (Some dirOfB))) admitOf isOk
    detectsOutputMutant unclassifiedIsAdmitted (inputFor checkoutA (call "a_tool_added_tomorrow" None None)) admitOf isError
    detectsOutputMutant doesNotResolveDetours (inputFor checkoutA (call onSession None (Some(dirOfA + "/../b")))) admitOf isError
    detectsOutputMutant unknownNamedSessionIsAdmitted (inputFor checkoutA (call onSession (Some "00000000") None)) admitOf isError
  ]

// ── Mutants of the order the mint is checked against ────────────────────

type OrderFn = RouteBinding * RouteBinding -> bool

let orderOf ((a, b): RouteBinding * RouteBinding) : bool = RouteBinding.isNarrowerOrEqual a b

let unboundIsTheBottom : Mutant<OrderFn> =
  { Name = "order_puts_unbound_at_the_bottom"
    Description = "Unbound must be the widest binding, or an Unbound request would pass as narrow"
    Apply = fun real (a, b) -> (match a with RouteBinding.Unbound -> true | _ -> real (a, b)) }

let anyTwoBoundAreRelated : Mutant<OrderFn> =
  { Name = "order_relates_any_two_bound_bindings"
    Description = "a token bound to A must not be able to mint one bound to B"
    Apply = fun real (a, b) ->
      match a, b with
      | RouteBinding.Unbound, _
      | _, RouteBinding.Unbound -> real (a, b)
      | _ -> true }

let sessionWithinAnyCheckout : Mutant<OrderFn> =
  { Name = "order_puts_a_session_inside_any_checkout"
    Description = "a session is not provably inside a checkout from the grant alone, so the order must not say so"
    Apply = fun real (a, b) ->
      match a, b with
      | RouteBinding.BoundToSession _, RouteBinding.BoundToCheckout _ -> true
      | _ -> real (a, b) }

[<Tests>]
let orderMutationTests =
  testList "RouteBinding.isNarrowerOrEqual mutants are caught" [
    detectsOutputMutant unboundIsTheBottom (RouteBinding.Unbound, checkoutA) orderOf not
    detectsOutputMutant anyTwoBoundAreRelated (checkoutB, checkoutA) orderOf not
    detectsOutputMutant anyTwoBoundAreRelated (RouteBinding.BoundToSession sessionB, sessionBoundA) orderOf not
    detectsOutputMutant sessionWithinAnyCheckout (sessionBoundA, checkoutA) orderOf not
  ]
