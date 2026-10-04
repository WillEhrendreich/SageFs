module SageFs.Tests.CapabilitySimTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Capability
open SageFs.Simulation
open SageFs.Simulation.CapabilitySim
open SageFs.Simulation.CapabilitySimInvariants

/// DST over the capability reducer: a conductor, a conductor under a narrow
/// token, a plain member and an anonymous caller minting, presenting, touching
/// and revoking tokens while the clock runs, folded through the REAL
/// `Capability.decide` and `resolve`. Chaos is data (a seeded event list); the
/// invariants check recorded outcomes against independent arithmetic.
///
/// Every invariant is shown to have teeth: a twin that breaks exactly that
/// guarantee must VIOLATE it, or the invariant would pass vacuously.
///  - NO-RAW-TOKEN-IN-LEDGER: the Phase 2 design (a token minted from the
///    entropy the ledger records) writes the token to disk.
///  - SCOPE-NEVER-WIDENS: a mint that checks the conductor's top authority
///    instead of the minter's own widens, and a claim check written as a plain
///    string prefix lets `src/Foo/../Bar` through.
///  - MINT-NEVER-SUBSTITUTES: a mint that clamps a wide request instead of
///    refusing it hands out something nobody asked for.
///  - REVOKED-STAYS-REVOKED: a touch that revives, and a mint over a revoked
///    hash, both bring a revoked token back.
///  - EXPIRY-IS-HONORED: a token that ignores its expiry and the idle lease.
///  - UNKNOWN-TOKEN-NEVER-RESOLVES: a bad token that falls back to the
///    connection's identity.

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 300 }

let private teethSeeds = [ 1 .. 300 ]

let private assertHolds (states: State list) =
  match violations states with
  | [] -> ()
  | vs -> failtestf "INVARIANT VIOLATION steps=%d\n  Violations=%A" (List.last states).Step vs

let private violatedBy (behavior: Behavior) (invariant: Invariant) (scenario: Scenario) =
  match invariant.Check(trace behavior scenario) with
  | Outcome.Violated _ -> true
  | Outcome.Holds -> false

let private someSeedBreaks (behavior: Behavior) (invariant: Invariant) =
  teethSeeds
  |> List.exists (fun seed -> violatedBy behavior invariant (scenarioOf seed))
  |> Expect.isTrue (sprintf "some seeded scenario must make the %A twin violate %s" behavior invariant.Id)

let private realHolds (invariant: Invariant) =
  for seed in teethSeeds do
    match invariant.Check(trace Behavior.Real (scenarioOf seed)) with
    | Outcome.Holds -> ()
    | Outcome.Violated message -> failtestf "%s must hold for the real reducer, seed %d: %s" invariant.Id seed message

// ── The spec: the same rules, written as plain arithmetic, as a twin of the reducer ──

type private SpecToken = {
  Preset: int
  Segments: string list
  NotAfter: DateTime
  LastSeen: DateTime
  RevokedAt: DateTime option
}

let private lifetimeMax = Timeouts.capabilityMaxLifetime

/// Folds the scenario's events through the spec and returns the verdict of every Mint and Present, in order.
let private specVerdicts (scenario: Scenario) : string list =
  let verdicts = ResizeArray<string>()
  let mutable clock = epoch
  let mutable tokens : Map<int, SpecToken> = Map.empty
  let segmentsOf (scope: ScopePrefix) = (ScopePrefix.value scope).Split('/', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray
  let isPrefix (shorter: string list) (longer: string list) = List.truncate shorter.Length longer = shorter
  let rank (preset: RolePreset) = RolePreset.all |> List.findIndex (fun p -> p = preset)
  let resolves (t: SpecToken) =
    match t.RevokedAt with
    | Some _ -> "revoked"
    | None ->
      if clock >= t.NotAfter then "expired"
      elif clock - t.LastSeen >= Cohort.leaseWindow then "lapsed"
      else "ok"
  for ev in scenario.Events do
    match ev with
    | SimEvent.PassMinutes n -> clock <- clock.AddMinutes(float n)
    | SimEvent.Mint(token, kind, preset, scope, route, lifetimeMinutes) ->
      let notAfter = clock.AddMinutes(float lifetimeMinutes)
      let isConductor = (kind = MinterKind.ConductorTop || kind = MinterKind.ConductorNarrow)
      let minterRank, minterSegments, minterNotAfter, minterRoute =
        match kind with
        | MinterKind.ConductorNarrow -> rank RolePreset.Analysis, [ "src"; "Foo" ], clock.AddHours 2.0, Some narrowConductorRoute
        | MinterKind.ConductorTop
        | MinterKind.PlainMember
        | MinterKind.Anonymous -> rank RolePreset.Implementer, [], DateTime.MaxValue, None
      // The route, as plain arithmetic: nobody mints an Unbound token, and a minter that is itself
      // bound can hand out only its own binding. A minter that is not bound hands out any bound one.
      let routeWidens = (match minterRoute with Some own -> route <> own | None -> false)
      let verdict =
        if not isConductor then "not-conductor"
        elif Map.containsKey token tokens then "duplicate"
        elif notAfter <= clock then "not-in-future"
        elif route = RouteBinding.Unbound then "unbound-not-mintable"
        elif rank preset > minterRank || not (isPrefix minterSegments (segmentsOf scope)) || notAfter > minterNotAfter || routeWidens then "would-widen"
        elif notAfter - clock > lifetimeMax then "too-long"
        else "ok"
      if verdict = "ok" then
        tokens <- Map.add token { Preset = rank preset; Segments = segmentsOf scope; NotAfter = notAfter; LastSeen = clock; RevokedAt = None } tokens
      verdicts.Add("mint:" + verdict)
    | SimEvent.Touch token ->
      match Map.tryFind token tokens with
      | Some t when resolves t = "ok" -> tokens <- Map.add token { t with LastSeen = clock } tokens
      | Some _
      | None -> ()
    | SimEvent.Revoke(token, byConductor) ->
      match Map.tryFind token tokens with
      | Some t when byConductor && t.RevokedAt.IsNone -> tokens <- Map.add token { t with RevokedAt = Some clock } tokens
      | Some _
      | None -> ()
    | SimEvent.Present token ->
      match Map.tryFind token tokens with
      | Some t -> verdicts.Add("present:" + resolves t)
      | None -> ()
    | SimEvent.PresentUnknown _ -> verdicts.Add "present:unknown"
    | SimEvent.Claim _ -> ()
    | SimEvent.Routed _ -> ()
  List.ofSeq verdicts

let private realVerdicts (scenario: Scenario) : string list =
  let final = List.last (trace Behavior.Real scenario)
  let mints =
    final.Mints
    |> List.map (fun m ->
      m.AtStep,
      "mint:"
      + (match m.Outcome with
         | Ok _ -> "ok"
         | Error MintRefusal.NotConductor -> "not-conductor"
         | Error MintRefusal.DuplicateToken -> "duplicate"
         | Error(MintRefusal.NotInTheFuture _) -> "not-in-future"
         | Error MintRefusal.UnboundNotMintable -> "unbound-not-mintable"
         | Error(MintRefusal.WouldWiden _) -> "would-widen"
         | Error(MintRefusal.LifetimeTooLong _) -> "too-long"))
  let presents =
    final.Presents
    |> List.map (fun p ->
      p.AtStep,
      "present:"
      + (match p.Resolution with
         | Resolution.AsToken _ -> "ok"
         | Resolution.Refused PresentRefusal.Unknown -> "unknown"
         | Resolution.Refused(PresentRefusal.Expired _) -> "expired"
         | Resolution.Refused(PresentRefusal.LeaseLapsed _) -> "lapsed"
         | Resolution.Refused(PresentRefusal.Revoked _) -> "revoked"
         | Resolution.AsConnection -> "connection"))
  mints @ presents |> List.sortBy fst |> List.map snd

[<Tests>]
let tests =
  testList "DST capability reducer" [

    testList "the real reducer holds every invariant" [

      testPropertyWithConfig simConfig "seeded scenarios hold every invariant" <|
        fun (seed: int) -> assertHolds (trace Behavior.Real (scenarioOf seed))

      testPropertyWithConfig simConfig "the real reducer agrees with the spec on every mint and every presentation" <|
        fun (seed: int) ->
          realVerdicts (scenarioOf seed)
          |> Expect.equal "the verdicts the reducer gave are the verdicts the arithmetic gives" (specVerdicts (scenarioOf seed))

      testCase "the sweep reaches every outcome, so agreement is not vacuous" <| fun () ->
        let all = teethSeeds |> List.collect (fun seed -> realVerdicts (scenarioOf seed)) |> Set.ofList
        for outcome in [ "mint:ok"; "mint:not-conductor"; "mint:would-widen"; "mint:too-long"; "mint:duplicate"; "mint:not-in-future"; "mint:unbound-not-mintable"; "present:ok"; "present:unknown"; "present:revoked"; "present:expired"; "present:lapsed" ] do
          all |> Set.contains outcome |> Expect.isTrue (sprintf "some scenario produces %s" outcome)

      testProperty "same seed, same trace" <|
        fun (seed: int) ->
          let a = trace Behavior.Real (scenarioOf seed)
          let b = trace Behavior.Real (scenarioOf seed)
          (List.last a).Ledger |> Expect.equal "identical ledger" (List.last b).Ledger
          (List.last a).Presents |> Expect.equal "identical presentations" (List.last b).Presents
    ]

    testList "NO-RAW-TOKEN-IN-LEDGER has teeth" [
      testCase "the real reducer never writes a token to a ledger row" <| fun () -> realHolds noRawTokenInLedger
      testCase "a token minted from the entropy the ledger records is in the ledger" <| fun () ->
        someSeedBreaks Behavior.MintsFromLedgerEntropyTwin noRawTokenInLedger
    ]

    testList "SCOPE-NEVER-WIDENS has teeth" [
      testCase "the real reducer never widens" <| fun () -> realHolds scopeNeverWidens
      testCase "a mint checked against the conductor's top authority widens a narrow minter" <| fun () ->
        someSeedBreaks Behavior.MintIgnoresMinterTwin scopeNeverWidens
      testCase "a claim checked with StartsWith on the raw path lets src/Foo/../Bar through" <| fun () ->
        someSeedBreaks Behavior.NaivePrefixClaimTwin scopeNeverWidens
    ]

    testList "MINT-NEVER-SUBSTITUTES has teeth" [
      testCase "the real reducer mints exactly what was asked or refuses" <| fun () -> realHolds mintNeverSubstitutes
      testCase "a mint that clamps instead of refusing hands out something nobody asked for" <| fun () ->
        someSeedBreaks Behavior.MintClampsTwin mintNeverSubstitutes
    ]

    testList "REVOKED-STAYS-REVOKED has teeth" [
      testCase "the real reducer never brings a revoked token back" <| fun () -> realHolds revokedStaysRevoked
      testCase "a touch that revives a revoked token breaks it" <| fun () ->
        someSeedBreaks Behavior.TouchRevivesTwin revokedStaysRevoked
      testCase "a mint over a revoked hash breaks it" <| fun () ->
        someSeedBreaks Behavior.MintOverwritesTwin revokedStaysRevoked
    ]

    testList "EXPIRY-IS-HONORED has teeth" [
      testCase "the real reducer honors expiry and the idle lease" <| fun () -> realHolds expiryIsHonored
      testCase "a token that ignores its expiry resolves after it" <| fun () ->
        someSeedBreaks Behavior.NeverExpiresTwin expiryIsHonored
    ]

    testList "UNKNOWN-TOKEN-NEVER-RESOLVES has teeth" [
      testCase "the real reducer refuses a token nobody minted" <| fun () -> realHolds unknownTokenNeverResolves
      testCase "a bad token that falls back to the connection is a downgrade" <| fun () ->
        someSeedBreaks Behavior.BadTokenFallsBackTwin unknownTokenNeverResolves
    ]

    testList "SCOPE-NEVER-WIDENS covers the route" [
      testCase "a mint whose widening check forgets the route lets a bound minter hand out another binding" <| fun () ->
        someSeedBreaks Behavior.MintIgnoresRouteTwin scopeNeverWidens
    ]

    testList "ROUTE-NEVER-ESCAPES-BINDING has teeth" [
      testCase "the real reducer admits a route only inside the token's binding" <| fun () -> realHolds routeNeverEscapesBinding
      testCase "a route check that ignores the binding admits a token for a session it was not bound to" <| fun () ->
        someSeedBreaks Behavior.RouteIgnoresBindingTwin routeNeverEscapesBinding
      testCase "the sweep reaches admitted and refused routes under every kind of binding, so the invariant is not vacuous" <| fun () ->
        let routes = teethSeeds |> List.collect (fun seed -> (List.last (trace Behavior.Real (scenarioOf seed))).Routes)
        routes |> List.exists (fun r -> r.Admitted) |> Expect.isTrue "some route is admitted"
        routes |> List.exists (fun r -> not r.Admitted) |> Expect.isTrue "some route is refused"
        for kind in [ "session"; "checkout" ] do
          let ofKind (r: RouteRecord) = (match r.Binding with RouteBinding.BoundToSession _ -> "session" | RouteBinding.BoundToCheckout _ -> "checkout" | RouteBinding.Unbound -> "unbound") = kind
          routes |> List.exists (fun r -> ofKind r && r.Admitted) |> Expect.isTrue (sprintf "an admitted route under a %s binding" kind)
          routes |> List.exists (fun r -> ofKind r && not r.Admitted) |> Expect.isTrue (sprintf "a refused route under a %s binding" kind)
    ]

    testList "ROUTE-REQUIRES-A-LIVE-TOKEN has teeth" [
      testCase "the real reducer routes only a token that is live" <| fun () -> realHolds routeRequiresALiveToken
      testCase "a route that skips resolving the token admits a revoked, expired or lapsed one" <| fun () ->
        someSeedBreaks Behavior.RouteSkipsResolveTwin routeRequiresALiveToken
    ]
  ]
