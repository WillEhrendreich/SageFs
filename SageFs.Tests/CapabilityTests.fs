module SageFs.Tests.CapabilityTests

/// The pure capability core (SageFs.Core/Capability.fs): per-run member tokens
/// the conductor mints. The raw token is shown once and never stored, so the
/// state holds hashes; a grant is a closed role preset, a canonical scope
/// prefix and an expiry; a grant can only be narrower than its minter's; a
/// revoked token stays revoked; the tool allow-list is a closed set of tool
/// classes, never free-form.

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.Cohort
open SageFs.MemberTable
open SageFs.Capability
open SageFs.Tests.SharedGenerators

let private epoch = DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc)
let private gateway = MemberId.Minted "gateway"
let private worker = MemberId.Minted "worker"
let private asConductor = Authority.Conductor gateway
let private asMember = Authority.Member(worker, JoinableRole.Implementer)

let private prefix (raw: string) : ScopePrefix =
  match ScopePrefix.tryParse raw with
  | Ok p -> p
  | Error refusal -> failwithf "test prefix %s is not valid: %A" raw refusal

let private grantOf (preset: RolePreset) (scope: string) (lifetime: TimeSpan) : Grant =
  { Preset = preset; Scope = prefix scope; NotAfter = epoch + lifetime }

let private hourOf (preset: RolePreset) (scope: string) = grantOf preset scope (TimeSpan.FromHours 1.0)

let private tokenOf (n: int) : string = Token.ofEntropy (Array.create Token.entropyBytes (byte n))
let private hashOf (n: int) : TokenHash = TokenHash.ofToken (tokenOf n)

let private conductorMinter = { Authority = asConductor; Grant = Grant.conductorAuthority }

let private mint (now: DateTime) (state: CapabilityState) (requested: Grant) (n: int) =
  decide now state (CapabilityCommand.Mint(conductorMinter, requested, hashOf n))

let private mintOk (now: DateTime) (state: CapabilityState) (requested: Grant) (n: int) : CapabilityState =
  match mint now state requested n with
  | Ok(next, _) -> next
  | Error refusal -> failwithf "mint %d was refused: %A" n refusal

let private resolveAt (now: DateTime) (state: CapabilityState) (n: int) = resolve now state (hashOf n)

let private idOf (n: int) : CapabilityId = CapabilityId.ofHash (hashOf n)

// ── Generators ────────────────────────────────────────────────────────────

let private genPreset = Gen.elements RolePreset.all

let private genPrefix : Gen<ScopePrefix> =
  Gen.elements [ ""; "src"; "src/Foo"; "src/Foo/Sub"; "src/Bar"; "tests" ]
  |> Gen.map prefix

let private genGrant : Gen<Grant> =
  gen {
    let! preset = genPreset
    let! scope = genPrefix
    let! minutes = Gen.choose (1, 600)
    return { Preset = preset; Scope = scope; NotAfter = epoch.AddMinutes(float minutes) }
  }

type private CapabilityGenerators =
  static member Grant() = Arb.fromGen genGrant
  static member Prefix() = Arb.fromGen genPrefix
  static member Preset() = Arb.fromGen genPreset

let private capConfig = { propConfig with arbitrary = [ typeof<CapabilityGenerators> ] }

[<Tests>]
let scopeTests =
  testList "Capability: scope prefix" [

    testCase "WHY - an empty, dot or ./ prefix is the whole repo" <| fun () ->
      for raw in [ ""; "."; "./"; "src/.." ] do
        ScopePrefix.tryParse raw |> Expect.equal (sprintf "'%s' is the root" raw) (Ok ScopePrefix.repoRoot)

    testCase "WHY - a prefix is canonical: src/Foo/ and src/Foo/../Foo are src/Foo" <| fun () ->
      ScopePrefix.tryParse "src/Foo/" |> Result.map ScopePrefix.value |> Expect.equal "trailing slash" (Ok "src/Foo")
      ScopePrefix.tryParse "src/Foo/../Foo" |> Result.map ScopePrefix.value |> Expect.equal "detour" (Ok "src/Foo")
      ScopePrefix.tryParse "src\\Foo" |> Result.map ScopePrefix.value |> Expect.equal "backslash" (Ok "src/Foo")

    testCase "WHY - a prefix that leaves the repo is refused" <| fun () ->
      ScopePrefix.tryParse "../Foo" |> Expect.equal "escape" (Error(PathRefusal.EscapesRoot "../Foo"))
      ScopePrefix.tryParse "/src/Foo" |> Expect.equal "absolute" (Error(PathRefusal.Absolute "/src/Foo"))

    testCase "WHY - a token minted for src/Foo/ covers src/Foo and nothing beside it" <| fun () ->
      let foo = prefix "src/Foo/"
      ScopePrefix.covers foo (ClaimScope.File "src/Foo/a.fs") |> Expect.isTrue "a file under it"
      ScopePrefix.covers foo (ClaimScope.File "src/Foo/Sub/b.fs") |> Expect.isTrue "a file deeper under it"
      ScopePrefix.covers foo (ClaimScope.Project "src/Foo/Foo.fsproj") |> Expect.isTrue "a project whose directory is it"
      ScopePrefix.covers foo (ClaimScope.Project "src/Foo/Sub/Sub.fsproj") |> Expect.isTrue "a project under it"
      ScopePrefix.covers foo (ClaimScope.File "src/Bar/a.fs") |> Expect.isFalse "src/Bar is a different directory"
      ScopePrefix.covers foo (ClaimScope.File "src/FooBar/a.fs") |> Expect.isFalse "FooBar only starts with the same letters"
      ScopePrefix.covers foo (ClaimScope.Project "src/Bar/Bar.fsproj") |> Expect.isFalse "another project"
      ScopePrefix.covers foo (ClaimScope.Project "Root.fsproj") |> Expect.isFalse "a project at the root is wider than src/Foo"

    testCase "WHY - src/Foo/../Bar/a.fs is src/Bar/a.fs, so a src/Foo token cannot claim it" <| fun () ->
      ScopePrefix.covers (prefix "src/Foo") (ClaimScope.File "src/Foo/../Bar/a.fs") |> Expect.isFalse "the detour is a different directory"
      ScopePrefix.covers (prefix "src/Foo") (ClaimScope.File "src/Foo/../Foo/a.fs") |> Expect.isTrue "but a detour that stays inside is fine"
      ScopePrefix.covers (prefix "src/Foo") (ClaimScope.File "src/Foo/../../a.fs") |> Expect.isFalse "and an escape is covered by nothing"

    testCase "WHY - the root prefix covers every path in the repo" <| fun () ->
      ScopePrefix.covers ScopePrefix.repoRoot (ClaimScope.File "a/b/c.fs") |> Expect.isTrue "file"
      ScopePrefix.covers ScopePrefix.repoRoot (ClaimScope.Project "Root.fsproj") |> Expect.isTrue "project at the root"

    testPropertyWithConfig capConfig "isWithin is a partial order" <| fun (a: ScopePrefix) (b: ScopePrefix) (c: ScopePrefix) ->
      let reflexive = ScopePrefix.isWithin a a
      let antisymmetric = not (ScopePrefix.isWithin a b && ScopePrefix.isWithin b a) || a = b
      let transitive = not (ScopePrefix.isWithin a b && ScopePrefix.isWithin b c) || ScopePrefix.isWithin a c
      reflexive && antisymmetric && transitive

    testPropertyWithConfig capConfig "a prefix covers exactly the files whose path segments start with its own" <| fun (p: ScopePrefix) (segmentCount: PositiveInt) ->
      let names = [| "src"; "Foo"; "Sub"; "Bar"; "tests"; "FooBar" |]
      let rng = Random(segmentCount.Get)
      let segments = [ for _ in 1 .. 1 + rng.Next 4 -> names.[rng.Next names.Length] ] @ [ "x.fs" ]
      let path = String.concat "/" segments
      let prefixSegments = (ScopePrefix.value p).Split('/', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray
      let expected = List.truncate prefixSegments.Length segments = prefixSegments
      ScopePrefix.covers p (ClaimScope.File path) = expected
  ]

[<Tests>]
let roleTests =
  testList "Capability: role presets and tool classes" [

    testCase "WHY - the presets form one chain, each strictly wider than the one before" <| fun () ->
      let chain = [ RolePreset.Observer; RolePreset.Analysis; RolePreset.Verifier; RolePreset.Implementer ]
      chain |> Expect.equal "RolePreset.all is that chain, narrowest first" RolePreset.all
      chain
      |> List.pairwise
      |> List.iter (fun (narrow, wide) ->
        RolePreset.isNarrowerOrEqual narrow wide |> Expect.isTrue (sprintf "%A is within %A" narrow wide)
        RolePreset.isNarrowerOrEqual wide narrow |> Expect.isFalse (sprintf "%A is not within %A" wide narrow)
        Set.isProperSubset (RolePreset.toolClasses narrow) (RolePreset.toolClasses wide)
        |> Expect.isTrue (sprintf "%A has strictly fewer tool classes than %A" narrow wide))

    testPropertyWithConfig capConfig "the role order is the subset order of tool classes" <| fun (a: RolePreset) (b: RolePreset) ->
      RolePreset.isNarrowerOrEqual a b = Set.isSubset (RolePreset.toolClasses a) (RolePreset.toolClasses b)

    testCase "WHY - no preset can administer the cohort or the machine" <| fun () ->
      for preset in RolePreset.all do
        let classes = RolePreset.toolClasses preset
        classes |> Set.contains ToolClass.CohortAdmin |> Expect.isFalse (sprintf "%A must not mint, revoke, reassign or configure" preset)
        classes |> Set.contains ToolClass.Maintenance |> Expect.isFalse (sprintf "%A must not clear local data or tidy the workspace" preset)

    testCase "WHY - a preset joins the cohort as the role it matches" <| fun () ->
      RolePreset.joinableRole RolePreset.Observer |> Expect.equal "Observer" JoinableRole.Observer
      RolePreset.joinableRole RolePreset.Analysis |> Expect.equal "Analysis reads, so it is an Observer" JoinableRole.Observer
      RolePreset.joinableRole RolePreset.Verifier |> Expect.equal "Verifier" JoinableRole.Verifier
      RolePreset.joinableRole RolePreset.Implementer |> Expect.equal "Implementer" JoinableRole.Implementer

    testCase "WHY - presets round-trip through their one to-string function and refuse anything else" <| fun () ->
      for preset in RolePreset.all do
        RolePreset.tryParse (RolePreset.toToken preset) |> Expect.equal "round trip" (Ok preset)
      RolePreset.tryParse "analysis" |> Expect.equal "case-insensitive" (Ok RolePreset.Analysis)
      (match RolePreset.tryParse "admin" with Error _ -> true | Ok _ -> false) |> Expect.isTrue "no free-form role"
      (RolePreset.all |> List.map RolePreset.toToken |> List.distinct |> List.length) |> Expect.equal "tokens are distinct" RolePreset.all.Length

    testCase "WHY - the tool classes are a closed list with one distinct token each" <| fun () ->
      let cases = Reflection.FSharpType.GetUnionCases typeof<ToolClass>
      ToolClass.all.Length |> Expect.equal "ToolClass.all names every union case" cases.Length
      (ToolClass.all |> List.map ToolClass.toToken |> List.distinct |> List.length) |> Expect.equal "tokens are distinct" cases.Length

    testCase "WHY - every tool the gate declares is classified, and no tool is in two classes" <| fun () ->
      for tool in Affordances.declaredGateTools do
        ToolClass.ofTool tool |> Option.isSome |> Expect.isTrue (sprintf "%s must be classified or no role can reach it" tool)
      let classed = ToolClass.all |> List.collect ToolClass.toolsOf
      (classed |> List.distinct |> List.length) |> Expect.equal "no tool is in two classes" classed.Length
      for tool in classed do
        ToolClass.ofTool tool |> Option.map (fun c -> ToolClass.toolsOf c |> List.contains tool) |> Expect.equal (sprintf "%s maps back to a class that lists it" tool) (Some true)

    testCase "WHY - an Analysis token is refused send_fsharp_code, and the refusal says which class and which role" <| fun () ->
      let analysis = hourOf RolePreset.Analysis "src/Foo"
      match admitTool analysis "send_fsharp_code" with
      | Error(ToolRefusal.NotInRole(tool, toolClass, preset)) ->
        tool |> Expect.equal "the tool" "send_fsharp_code"
        toolClass |> Expect.equal "evaluation is its own class" ToolClass.Eval
        preset |> Expect.equal "the role" RolePreset.Analysis
      | other -> failtestf "expected NotInRole, got %A" other

    testCase "WHY - what each preset may call" <| fun () ->
      let may preset tool = admitTool (hourOf preset "") tool |> Result.isOk
      may RolePreset.Observer "get_cohort_status" |> Expect.isTrue "Observer reads the cohort"
      may RolePreset.Observer "diagnose" |> Expect.isFalse "Observer does not analyse"
      may RolePreset.Analysis "diagnose" |> Expect.isTrue "Analysis analyses"
      may RolePreset.Analysis "check_fsharp_code" |> Expect.isTrue "type-checking is analysis"
      for refused in [ "send_fsharp_code"; "run_tests"; "hard_reset_fsi_session"; "run_app"; "acquire_claim"; "mint_member"; "tidy_workspace" ] do
        may RolePreset.Analysis refused |> Expect.isFalse (sprintf "Analysis must not call %s" refused)
      may RolePreset.Verifier "run_tests" |> Expect.isTrue "Verifier runs tests"
      may RolePreset.Verifier "send_fsharp_code" |> Expect.isFalse "Verifier does not evaluate"
      may RolePreset.Implementer "send_fsharp_code" |> Expect.isTrue "Implementer evaluates"
      may RolePreset.Implementer "acquire_claim" |> Expect.isTrue "Implementer claims"
      for conductorOnly in [ "mint_member"; "revoke_member"; "reassign_claim"; "set_integration_ref"; "manage_local_data"; "tidy_workspace" ] do
        may RolePreset.Implementer conductorOnly |> Expect.isFalse (sprintf "Implementer must not call %s" conductorOnly)

    testCase "WHY - a tool nobody classified is refused, never allowed by default" <| fun () ->
      admitTool (hourOf RolePreset.Implementer "") "brand_new_tool"
      |> Expect.equal "fail closed" (Error(ToolRefusal.Unclassified "brand_new_tool"))

    testCase "WHY - tools/list for an Analysis token leaves out the tools it cannot call" <| fun () ->
      let visible = visibleTools (hourOf RolePreset.Analysis "") Affordances.declaredGateTools
      visible |> List.contains "diagnose" |> Expect.isTrue "analysis tools stay"
      visible |> List.contains "send_fsharp_code" |> Expect.isFalse "eval goes"
      visible |> List.contains "mint_member" |> Expect.isFalse "minting goes"
      visible |> Expect.all "everything listed is callable" (fun tool -> admitTool (hourOf RolePreset.Analysis "") tool |> Result.isOk)
  ]

[<Tests>]
let grantOrderTests =
  testList "Capability: a grant is never wider than its minter's" [

    testCase "WHY - the conductor's authority is above every grant" <| fun () ->
      Grant.isNarrowerOrEqual (grantOf RolePreset.Implementer "" (Timeouts.capabilityMaxLifetime)) Grant.conductorAuthority
      |> Expect.isTrue "the widest grant a minted token can be is still narrower than the conductor"

    testPropertyWithConfig capConfig "narrower-or-equal is reflexive and transitive" <| fun (a: Grant) (b: Grant) (c: Grant) ->
      Grant.isNarrowerOrEqual a a
      && (not (Grant.isNarrowerOrEqual a b && Grant.isNarrowerOrEqual b c) || Grant.isNarrowerOrEqual a c)

    testPropertyWithConfig capConfig "narrower-or-equal is antisymmetric" <| fun (a: Grant) (b: Grant) ->
      not (Grant.isNarrowerOrEqual a b && Grant.isNarrowerOrEqual b a) || a = b

    testPropertyWithConfig capConfig "a grant is within its minter's exactly when it widens nothing" <| fun (requested: Grant) (minter: Grant) ->
      Grant.isNarrowerOrEqual requested minter = List.isEmpty (Grant.wideningsOf requested minter)

    testCase "WHY - each way a request is wider is named" <| fun () ->
      let minter = grantOf RolePreset.Analysis "src/Foo" (TimeSpan.FromHours 1.0)
      let requested = grantOf RolePreset.Implementer "src" (TimeSpan.FromHours 2.0)
      Grant.wideningsOf requested minter
      |> Expect.equal
        "role, scope and expiry"
        [ Widening.Role(RolePreset.Implementer, RolePreset.Analysis)
          Widening.Scope(prefix "src", prefix "src/Foo")
          Widening.Expiry(requested.NotAfter, minter.NotAfter) ]
  ]

[<Tests>]
let tokenTests =
  testList "Capability: the token is shown once, only its hash is kept" [

    testCase "WHY - a token is derived from entropy, and different entropy is a different token" <| fun () ->
      tokenOf 1 |> Expect.equal "deterministic" (tokenOf 1)
      (tokenOf 1 <> tokenOf 2) |> Expect.isTrue "distinct"
      Token.ofEntropy [||] |> fun t -> t.Length > 0 |> Expect.isTrue "even empty entropy yields a string, so callers cannot mint nothing by mistake"

    testCase "WHY - the hash is not the token and does not contain it" <| fun () ->
      let token = tokenOf 7
      let hash = TokenHash.value (TokenHash.ofToken token)
      hash.Contains token |> Expect.isFalse "no token in the hash"
      hash.Length |> Expect.equal "SHA-256 as hex" 64

    testCase "WHY - the public id is a prefix of the hash and maps to a cap: member id" <| fun () ->
      let id = idOf 3
      (TokenHash.value (hashOf 3)).StartsWith(CapabilityId.value id) |> Expect.isTrue "a prefix of the hash"
      MemberId.display (CapabilityId.memberId id) |> Expect.equal "display" ("cap:" + CapabilityId.value id)
      CapabilityId.tryOfMemberId (CapabilityId.memberId id) |> Expect.equal "round trip" (Some id)
      CapabilityId.tryOfMemberId (MemberId.Minted "x") |> Expect.equal "other members are not capabilities" None

    testCase "WHY - nothing the reducer returns contains the raw token" <| fun () ->
      let token = tokenOf 9
      let hash = TokenHash.ofToken token
      let command = CapabilityCommand.Mint(conductorMinter, hourOf RolePreset.Analysis "src/Foo", hash)
      match decide epoch CapabilityState.empty command with
      | Ok(state, events) ->
        let everything = sprintf "%A %A %A" command state events
        everything.Contains token |> Expect.isFalse "the command, the state and the events carry the hash, never the token"
        everything.Contains(tokenOf 9 |> fun t -> t.Substring(4)) |> Expect.isFalse "nor the body of it"
      | Error refusal -> failtestf "mint refused: %A" refusal
  ]

[<Tests>]
let reducerTests =
  testList "Capability: mint, present, revoke" [

    testCase "WHY - the conductor mints a narrower grant and the token then resolves" <| fun () ->
      let grant = hourOf RolePreset.Analysis "src/Foo"
      let state = mintOk epoch CapabilityState.empty grant 1
      match resolveAt epoch state 1 with
      | Ok record ->
        record.Grant |> Expect.equal "the grant as asked" grant
        record.Id |> Expect.equal "its public id" (idOf 1)
        record.MintedBy |> Expect.equal "by the conductor" gateway
        record.Status |> Expect.equal "active" CapabilityStatus.Active
      | Error refusal -> failtestf "should resolve: %A" refusal

    testCase "WHY - a member who is not the conductor cannot mint" <| fun () ->
      let minter = { Authority = asMember; Grant = Grant.conductorAuthority }
      decide epoch CapabilityState.empty (CapabilityCommand.Mint(minter, hourOf RolePreset.Observer "", hashOf 1))
      |> Expect.equal "refused" (Error(CapabilityRefusal.Mint MintRefusal.NotConductor))
      let anonymous = { Authority = Authority.Anonymous; Grant = Grant.conductorAuthority }
      decide epoch CapabilityState.empty (CapabilityCommand.Mint(anonymous, hourOf RolePreset.Observer "", hashOf 1))
      |> Expect.equal "refused" (Error(CapabilityRefusal.Mint MintRefusal.NotConductor))

    testCase "WHY - a minter whose own grant is narrow cannot hand out a wider one, and nothing is clamped" <| fun () ->
      let own = hourOf RolePreset.Analysis "src/Foo"
      let minter = { Authority = asConductor; Grant = own }
      let wider = hourOf RolePreset.Implementer "src"
      match decide epoch CapabilityState.empty (CapabilityCommand.Mint(minter, wider, hashOf 1)) with
      | Error(CapabilityRefusal.Mint(MintRefusal.WouldWiden widenings)) ->
        widenings |> List.length |> Expect.equal "role and scope" 2
      | other -> failtestf "expected WouldWiden, got %A" other
      decide epoch CapabilityState.empty (CapabilityCommand.Mint(minter, own, hashOf 1)) |> Result.isOk |> Expect.isTrue "the same grant is fine"

    testCase "WHY - a token cannot be minted that is already expired or lives past the maximum lifetime" <| fun () ->
      mint epoch CapabilityState.empty (grantOf RolePreset.Observer "" TimeSpan.Zero) 1
      |> Expect.equal "expired on arrival" (Error(CapabilityRefusal.Mint(MintRefusal.NotInTheFuture epoch)))
      mint epoch CapabilityState.empty (grantOf RolePreset.Observer "" (Timeouts.capabilityMaxLifetime + TimeSpan.FromMinutes 1.0)) 1
      |> Expect.equal "too long" (Error(CapabilityRefusal.Mint(MintRefusal.LifetimeTooLong(Timeouts.capabilityMaxLifetime + TimeSpan.FromMinutes 1.0, Timeouts.capabilityMaxLifetime))))
      mint epoch CapabilityState.empty (grantOf RolePreset.Observer "" Timeouts.capabilityMaxLifetime) 1
      |> Result.isOk |> Expect.isTrue "exactly the maximum is allowed"
      Timeouts.capabilityDefaultLifetime <= Timeouts.capabilityMaxLifetime |> Expect.isTrue "the default fits under the maximum"

    testCase "WHY - the same token hash cannot be minted twice, even after revocation" <| fun () ->
      let state = mintOk epoch CapabilityState.empty (hourOf RolePreset.Observer "") 1
      mint epoch state (hourOf RolePreset.Observer "") 1 |> Expect.equal "twice" (Error(CapabilityRefusal.Mint MintRefusal.DuplicateToken))
      let revoked = match decide epoch state (CapabilityCommand.Revoke(asConductor, idOf 1)) with Ok(s, _) -> s | Error e -> failtestf "%A" e
      mint epoch revoked (hourOf RolePreset.Observer "") 1 |> Expect.equal "after revoke" (Error(CapabilityRefusal.Mint MintRefusal.DuplicateToken))

    testCase "WHY - an unknown token resolves to nothing" <| fun () ->
      resolveAt epoch CapabilityState.empty 1 |> Expect.equal "unknown" (Error PresentRefusal.Unknown)
      let state = mintOk epoch CapabilityState.empty (hourOf RolePreset.Observer "") 1
      resolveAt epoch state 2 |> Expect.equal "another token" (Error PresentRefusal.Unknown)

    testCase "WHY - a token stops working at its expiry" <| fun () ->
      let grant = grantOf RolePreset.Observer "" (TimeSpan.FromMinutes 20.0)
      let state = mintOk epoch CapabilityState.empty grant 1
      resolveAt (epoch.AddMinutes 19.0) state 1 |> Result.isOk |> Expect.isTrue "just before"
      resolveAt grant.NotAfter state 1 |> Expect.equal "at the instant" (Error(PresentRefusal.Expired grant.NotAfter))
      resolveAt (grant.NotAfter.AddHours 1.0) state 1 |> Expect.equal "long after" (Error(PresentRefusal.Expired grant.NotAfter))

    testCase "WHY - a token that is not used for a lease window lapses, and using it keeps it alive up to its expiry" <| fun () ->
      let grant = hourOf RolePreset.Observer ""
      let state = mintOk epoch CapabilityState.empty grant 1
      let idle = epoch + Cohort.leaseWindow
      resolveAt idle state 1 |> Expect.equal "idle a whole lease window" (Error(PresentRefusal.LeaseLapsed epoch))
      // Used every ten minutes, it is still good after the first lease window has passed.
      let used =
        [ 10.0; 20.0; 30.0; 40.0; 50.0 ]
        |> List.fold (fun s minutes -> match decide (epoch.AddMinutes minutes) s (CapabilityCommand.Touch(hashOf 1)) with Ok(next, _) -> next | Error e -> failtestf "%A" e) state
      resolveAt (epoch.AddMinutes 55.0) used 1 |> Result.isOk |> Expect.isTrue "kept alive past 30 minutes"
      resolveAt grant.NotAfter used 1 |> Expect.equal "but never past its own expiry" (Error(PresentRefusal.Expired grant.NotAfter))

    testCase "WHY - Touch on a lapsed token does not bring it back" <| fun () ->
      let state = mintOk epoch CapabilityState.empty (hourOf RolePreset.Observer "") 1
      let late = epoch + Cohort.leaseWindow + TimeSpan.FromMinutes 1.0
      match decide late state (CapabilityCommand.Touch(hashOf 1)) with
      | Ok(next, events) ->
        events |> Expect.isEmpty "nothing was touched"
        resolveAt late next 1 |> Expect.equal "still lapsed" (Error(PresentRefusal.LeaseLapsed epoch))
      | Error e -> failtestf "touch should not refuse: %A" e

    testCase "WHY - revoking stops the token, and it stays stopped" <| fun () ->
      let state = mintOk epoch CapabilityState.empty (hourOf RolePreset.Implementer "src") 1
      let at = epoch.AddMinutes 5.0
      match decide at state (CapabilityCommand.Revoke(asConductor, idOf 1)) with
      | Ok(revoked, events) ->
        events |> Expect.equal "the event" [ CapabilityEvent.Revoked(idOf 1) ]
        resolveAt at revoked 1 |> Expect.equal "refused at once" (Error(PresentRefusal.Revoked at))
        // Using it, waiting, and trying to touch it again changes nothing.
        let touched = match decide (at.AddMinutes 1.0) revoked (CapabilityCommand.Touch(hashOf 1)) with Ok(s, _) -> s | Error e -> failtestf "%A" e
        resolveAt (at.AddMinutes 2.0) touched 1 |> Expect.equal "still revoked after a touch" (Error(PresentRefusal.Revoked at))
        resolveAt (epoch.AddHours 10.0) touched 1 |> Expect.equal "still revoked, and revocation outranks expiry" (Error(PresentRefusal.Revoked at))
      | Error e -> failtestf "revoke refused: %A" e

    testCase "WHY - only the conductor revokes, only a known token, and only once" <| fun () ->
      let state = mintOk epoch CapabilityState.empty (hourOf RolePreset.Observer "") 1
      decide epoch state (CapabilityCommand.Revoke(asMember, idOf 1)) |> Expect.equal "a member" (Error(CapabilityRefusal.Revoke RevokeRefusal.NotConductor))
      decide epoch state (CapabilityCommand.Revoke(asConductor, idOf 2)) |> Expect.equal "unknown" (Error(CapabilityRefusal.Revoke(RevokeRefusal.UnknownCapability(idOf 2))))
      let revoked = match decide epoch state (CapabilityCommand.Revoke(asConductor, idOf 1)) with Ok(s, _) -> s | Error e -> failtestf "%A" e
      decide (epoch.AddMinutes 1.0) revoked (CapabilityCommand.Revoke(asConductor, idOf 1))
      |> Expect.equal "twice" (Error(CapabilityRefusal.Revoke(RevokeRefusal.AlreadyRevoked epoch)))

    testCase "WHY - a token minted for src/Foo cannot claim src/Bar, and a detour through .. does not help" <| fun () ->
      let grant = hourOf RolePreset.Implementer "src/Foo/"
      admitClaim grant (ClaimScope.File "src/Foo/a.fs") |> Result.isOk |> Expect.isTrue "inside"
      admitClaim grant (ClaimScope.Project "src/Foo/Foo.fsproj") |> Result.isOk |> Expect.isTrue "inside, a project"
      admitClaim grant (ClaimScope.File "src/Bar/a.fs")
      |> Expect.equal "outside" (Error(ScopeRefusal.OutsideScope(ClaimScope.File "src/Bar/a.fs", prefix "src/Foo")))
      admitClaim grant (ClaimScope.File "src/Foo/../Bar/a.fs")
      |> Expect.equal "the detour is src/Bar" (Error(ScopeRefusal.OutsideScope(ClaimScope.File "src/Bar/a.fs", prefix "src/Foo")))
      admitClaim grant (ClaimScope.File "../a.fs")
      |> Expect.equal "an escape" (Error(ScopeRefusal.MalformedPath(PathRefusal.EscapesRoot "../a.fs")))
  ]

[<Tests>]
let identityPolicyTests =
  testList "Capability: identity policy for a connection with no token" [

    testCase "WHY - the default changes nothing: a token-less connection is a plain member" <| fun () ->
      IdentityPolicy.defaultPolicy |> Expect.equal "default" IdentityPolicy.ConnectionsAllowed
      for tool in Affordances.declaredGateTools do
        for authority in [ Authority.Anonymous; asMember; asConductor ] do
          admitTokenless IdentityPolicy.ConnectionsAllowed authority tool |> Expect.equal (sprintf "%s is allowed" tool) (Ok ())

    testCase "WHY - when a token is required a token-less connection may only read status, unless it is the conductor" <| fun () ->
      let policy = IdentityPolicy.TokenRequired
      admitTokenless policy Authority.Anonymous "get_cohort_status" |> Expect.equal "status" (Ok ())
      admitTokenless policy Authority.Anonymous "get_daemon_status" |> Expect.equal "daemon status" (Ok ())
      admitTokenless policy asMember "send_fsharp_code" |> Expect.equal "a plain member is refused" (Error(PolicyRefusal.TokenRequired "send_fsharp_code"))
      admitTokenless policy Authority.Anonymous "join_cohort" |> Expect.equal "and cannot join" (Error(PolicyRefusal.TokenRequired "join_cohort"))
      admitTokenless policy asConductor "mint_member" |> Expect.equal "the conductor may act, so it can mint" (Ok ())
      admitTokenless policy asConductor "send_fsharp_code" |> Expect.equal "the conductor may act" (Ok ())

    testCase "WHY - policies round-trip through one to-string function" <| fun () ->
      for policy in IdentityPolicy.all do
        IdentityPolicy.tryParse (IdentityPolicy.toToken policy) |> Expect.equal "round trip" (Ok policy)
      (match IdentityPolicy.tryParse "yolo" with Error _ -> true | Ok _ -> false) |> Expect.isTrue "no other policy"
      IdentityPolicy.tryParse "tokenrequired" |> Expect.equal "case-insensitive" (Ok IdentityPolicy.TokenRequired)
  ]
