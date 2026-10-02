namespace SageFs.Simulation

open System
open SageFs
open SageFs.Cohort
open SageFs.MemberTable
open SageFs.Capability

/// Deterministic Simulation Testing for the capability reducer: a conductor, a
/// conductor acting under a narrow token, a plain member and an anonymous
/// caller minting, presenting, touching and revoking tokens while the clock
/// runs, folded through the REAL `Capability.decide` and `resolve`. Same rules
/// as the other DST harnesses here: chaos is data (a seeded event list), the
/// real functions are the subject, and every twin breaks exactly one guarantee
/// so the invariant that guards it has teeth:
///   - `MintsFromLedgerEntropyTwin` is the Phase 2 design of
///     cohort-member-identity-as-capability.md: the token is derived from the
///     entropy the ledger records, so the ledger holds the token.
///   - `MintIgnoresMinterTwin` checks a request against the conductor's top
///     authority instead of the minter's own, so a narrow minter widens.
///   - `MintClampsTwin` clamps a wider request instead of refusing it.
///   - `NaivePrefixClaimTwin` checks a claim with `StartsWith` on the raw path,
///     the way a scope check written without canonicalization would.
///   - `TouchRevivesTwin` lets an accepted touch make a revoked token active.
///   - `MintOverwritesTwin` lets a hash be minted again over a revoked record.
///   - `NeverExpiresTwin` ignores `NotAfter` and the idle lease.
///   - `BadTokenFallsBackTwin` resolves a token nobody minted as the plain
///     connection, the downgrade a presented-but-bad token must never get.
module CapabilitySim =

  /// Who is asking to mint.
  [<RequireQualifiedAccess>]
  type MinterKind =
    /// The conductor on its own connection: everything, everywhere.
    | ConductorTop
    /// The conductor acting under a token: an Analysis grant over src/Foo.
    | ConductorNarrow
    | PlainMember
    | Anonymous

  [<RequireQualifiedAccess>]
  type SimEvent =
    /// Mint token number `token` as `minter`, asking for `preset` over `scope`, valid for `lifetimeMinutes` from now.
    | Mint of token: int * minter: MinterKind * preset: RolePreset * scope: ScopePrefix * lifetimeMinutes: int
    | Touch of token: int
    | Revoke of token: int * byConductor: bool
    | Present of token: int
    /// Present a token nobody minted.
    | PresentUnknown of n: int
    /// The token holder tries to claim `scope`.
    | Claim of token: int * scope: ClaimScope
    | PassMinutes of int

  [<RequireQualifiedAccess>]
  type Behavior =
    | Real
    | MintsFromLedgerEntropyTwin
    | MintIgnoresMinterTwin
    | MintClampsTwin
    | NaivePrefixClaimTwin
    | TouchRevivesTwin
    | MintOverwritesTwin
    | NeverExpiresTwin
    | BadTokenFallsBackTwin

  [<RequireQualifiedAccess>]
  type Resolution =
    | AsToken of CapabilityRecord
    | Refused of PresentRefusal
    /// The call fell back to the connection's own identity. Real never does this.
    | AsConnection

  /// What the persisted cohort ledger would hold for an accepted command: its
  /// text, and the entropy it recorded so `replay` is deterministic.
  type LedgerRow = { Step: int; Text: string; Entropy: byte[] }

  type MintRecord = {
    AtStep: int
    At: DateTime
    Token: int
    Minter: MinterKind
    MinterGrant: Grant
    Requested: Grant
    Outcome: Result<CapabilityRecord, MintRefusal>
  }

  type PresentRecord = {
    AtStep: int
    At: DateTime
    /// `None` for a token nobody minted.
    Token: int option
    Resolution: Resolution
    /// When this token was last accepted before this presentation, by the sim's own bookkeeping.
    LastAcceptedBefore: DateTime option
  }

  type ClaimRecord = {
    AtStep: int
    Token: int
    Scope: ClaimScope
    Grant: Grant
    Allowed: bool
  }

  type State = {
    Step: int
    Clock: DateTime
    Capabilities: CapabilityState
    /// The raw token of each minted number: what the edge showed once.
    RawTokens: Map<int, string>
    /// The entropy behind each raw token.
    TokenEntropy: Map<int, byte[]>
    /// When each token was last accepted, tracked here and not read from the reducer, so an invariant can check the reducer against it.
    LastAccepted: Map<int, DateTime>
    Ledger: LedgerRow list
    Mints: MintRecord list
    Presents: PresentRecord list
    Claims: ClaimRecord list
    /// Steps at which each token was revoked, by an accepted revoke.
    RevokedAtStep: Map<int, int>
  }

  let epoch = DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc)

  let initial : State = {
    Step = 0
    Clock = epoch
    Capabilities = CapabilityState.empty
    RawTokens = Map.empty
    TokenEntropy = Map.empty
    LastAccepted = Map.empty
    Ledger = []
    Mints = []
    Presents = []
    Claims = []
    RevokedAtStep = Map.empty
  }

  let private conductorId = MemberId.Minted "conductor"

  let narrowConductorGrant (now: DateTime) : Grant =
    let scope = match ScopePrefix.tryParse "src/Foo" with Ok p -> p | Error _ -> ScopePrefix.repoRoot
    { Preset = RolePreset.Analysis; Scope = scope; NotAfter = now.AddHours 2.0 }

  let private minterOf (kind: MinterKind) (now: DateTime) : Minter =
    match kind with
    | MinterKind.ConductorTop -> { Authority = Authority.Conductor conductorId; Grant = Grant.conductorAuthority }
    | MinterKind.ConductorNarrow -> { Authority = Authority.Conductor conductorId; Grant = narrowConductorGrant now }
    | MinterKind.PlainMember ->
      { Authority = Authority.Member(MemberId.Minted "member", JoinableRole.Implementer); Grant = Grant.conductorAuthority }
    | MinterKind.Anonymous -> { Authority = Authority.Anonymous; Grant = Grant.conductorAuthority }

  /// The 32 bytes the edge would draw for token `n` of a scenario: deterministic, so a scenario replays.
  let private entropyOf (n: int) : byte[] =
    Security.Cryptography.SHA256.HashData(Text.Encoding.UTF8.GetBytes(sprintf "capability-sim-token-%d" n))

  let private ledgerRowOf (behavior: Behavior) (step: int) (command: CapabilityCommand) (events: CapabilityEvent list) (tokenEntropy: byte[]) : LedgerRow =
    // What a cohort ledger row holds: the command and its events as text, and the
    // entropy the command used so that `replay` is deterministic. Real commands are
    // pure and use none that matters, so it is the step counter. The Phase 2 design
    // mints from that entropy, so the entropy IS the token.
    let entropy =
      match behavior with
      | Behavior.MintsFromLedgerEntropyTwin -> tokenEntropy
      | Behavior.Real
      | Behavior.MintIgnoresMinterTwin
      | Behavior.MintClampsTwin
      | Behavior.NaivePrefixClaimTwin
      | Behavior.TouchRevivesTwin
      | Behavior.MintOverwritesTwin
      | Behavior.NeverExpiresTwin
      | Behavior.BadTokenFallsBackTwin -> BitConverter.GetBytes step
    { Step = step; Text = sprintf "%A %A" command events; Entropy = entropy }

  /// Every spelling a raw token or its entropy could take in a ledger row.
  let spellingsOf (rawToken: string) (entropy: byte[]) : string list =
    [ rawToken
      rawToken.Substring Token.prefix.Length
      Convert.ToBase64String entropy
      Convert.ToHexString entropy
      Convert.ToHexString(entropy).ToLowerInvariant() ]

  let rowSpellings (row: LedgerRow) : string =
    String.concat "\n" [ row.Text; Convert.ToBase64String row.Entropy; Convert.ToHexString row.Entropy; Convert.ToHexString(row.Entropy).ToLowerInvariant() ]

  // ── The twins' reducers ───────────────────────────────────────────────────

  let private touchRevives (now: DateTime) (state: CapabilityState) (hash: TokenHash) : CapabilityState =
    match Map.tryFind hash state.Records with
    | Some record -> { state with Records = Map.add hash { record with LastSeen = now; Status = CapabilityStatus.Active } state.Records }
    | None -> state

  let private mintOverwrites (now: DateTime) (state: CapabilityState) (minter: Minter) (requested: Grant) (hash: TokenHash) =
    // Drop a revoked record first, so the real mint sees a fresh hash.
    let cleared =
      match Map.tryFind hash state.Records with
      | Some { Status = CapabilityStatus.Revoked _ } -> { state with Records = Map.remove hash state.Records }
      | _ -> state
    decide now cleared (CapabilityCommand.Mint(minter, requested, hash))

  let private clampTo (minter: Grant) (requested: Grant) : Grant =
    let preset = if RolePreset.isNarrowerOrEqual requested.Preset minter.Preset then requested.Preset else minter.Preset
    let scope = if ScopePrefix.isWithin requested.Scope minter.Scope then requested.Scope else minter.Scope
    { Preset = preset; Scope = scope; NotAfter = min requested.NotAfter minter.NotAfter }

  let private resolveNeverExpires (now: DateTime) (state: CapabilityState) (hash: TokenHash) : Result<CapabilityRecord, PresentRefusal> =
    match Map.tryFind hash state.Records with
    | None -> Error PresentRefusal.Unknown
    | Some record ->
      match record.Status with
      | CapabilityStatus.Revoked at -> Error(PresentRefusal.Revoked at)
      | CapabilityStatus.Active -> Ok record

  // ── Folding one event ─────────────────────────────────────────────────────

  let private rawTokenFor (state: State) (n: int) : string * byte[] =
    match Map.tryFind n state.RawTokens, Map.tryFind n state.TokenEntropy with
    | Some raw, Some entropy -> raw, entropy
    | _ ->
      let entropy = entropyOf n
      Token.ofEntropy entropy, entropy

  let step (behavior: Behavior) (s: State) (ev: SimEvent) : State =
    let s = { s with Step = s.Step + 1 }
    match ev with
    | SimEvent.PassMinutes n -> { s with Clock = s.Clock.AddMinutes(float n) }
    | SimEvent.Mint(token, kind, preset, scope, lifetimeMinutes) ->
      let request = { Preset = preset; Scope = scope; NotAfter = s.Clock.AddMinutes(float lifetimeMinutes) }
      let raw, entropy = rawTokenFor s token
      let hash = TokenHash.ofToken raw
      let minter = minterOf kind s.Clock
      let command = CapabilityCommand.Mint(minter, request, hash)
      let outcome =
        match behavior with
        | Behavior.MintIgnoresMinterTwin ->
          decide s.Clock s.Capabilities (CapabilityCommand.Mint({ minter with Grant = Grant.conductorAuthority }, request, hash))
        | Behavior.MintClampsTwin ->
          decide s.Clock s.Capabilities (CapabilityCommand.Mint(minter, clampTo minter.Grant request, hash))
        | Behavior.MintOverwritesTwin -> mintOverwrites s.Clock s.Capabilities minter request hash
        | Behavior.Real
        | Behavior.MintsFromLedgerEntropyTwin
        | Behavior.NaivePrefixClaimTwin
        | Behavior.TouchRevivesTwin
        | Behavior.NeverExpiresTwin
        | Behavior.BadTokenFallsBackTwin -> decide s.Clock s.Capabilities command
      let recorded record = { AtStep = s.Step; At = s.Clock; Token = token; Minter = kind; MinterGrant = minter.Grant; Requested = request; Outcome = record }
      match outcome with
      | Ok(next, events) ->
        let record = next.Records.[hash]
        { s with
            Capabilities = next
            RawTokens = Map.add token raw s.RawTokens
            TokenEntropy = Map.add token entropy s.TokenEntropy
            LastAccepted = Map.add token s.Clock s.LastAccepted
            Ledger = s.Ledger @ [ ledgerRowOf behavior s.Step command events entropy ]
            Mints = s.Mints @ [ recorded (Ok record) ] }
      | Error(CapabilityRefusal.Mint refusal) -> { s with Mints = s.Mints @ [ recorded (Error refusal) ] }
      | Error(CapabilityRefusal.Revoke _) -> s
    | SimEvent.Touch token ->
      match Map.tryFind token s.RawTokens with
      | None -> s
      | Some raw ->
        let hash = TokenHash.ofToken raw
        let command = CapabilityCommand.Touch hash
        match behavior with
        | Behavior.TouchRevivesTwin ->
          { s with Capabilities = touchRevives s.Clock s.Capabilities hash; LastAccepted = Map.add token s.Clock s.LastAccepted }
        | Behavior.Real
        | Behavior.MintsFromLedgerEntropyTwin
        | Behavior.MintIgnoresMinterTwin
        | Behavior.MintClampsTwin
        | Behavior.NaivePrefixClaimTwin
        | Behavior.MintOverwritesTwin
        | Behavior.NeverExpiresTwin
        | Behavior.BadTokenFallsBackTwin ->
          match decide s.Clock s.Capabilities command with
          | Ok(next, events) ->
            let accepted = if List.isEmpty events then s.LastAccepted else Map.add token s.Clock s.LastAccepted
            { s with Capabilities = next; LastAccepted = accepted }
          | Error _ -> s
    | SimEvent.Revoke(token, byConductor) ->
      match Map.tryFind token s.RawTokens with
      | None -> s
      | Some raw ->
        let id = CapabilityId.ofHash (TokenHash.ofToken raw)
        let by = if byConductor then Authority.Conductor conductorId else Authority.Member(MemberId.Minted "member", JoinableRole.Implementer)
        let command = CapabilityCommand.Revoke(by, id)
        match decide s.Clock s.Capabilities command with
        | Ok(next, events) ->
          { s with
              Capabilities = next
              Ledger = s.Ledger @ [ ledgerRowOf behavior s.Step command events (entropyOf token) ]
              RevokedAtStep = if Map.containsKey token s.RevokedAtStep then s.RevokedAtStep else Map.add token s.Step s.RevokedAtStep }
        | Error _ -> s
    | SimEvent.Present token ->
      match Map.tryFind token s.RawTokens with
      | None -> s
      | Some raw ->
        let hash = TokenHash.ofToken raw
        let resolved =
          match behavior with
          | Behavior.NeverExpiresTwin -> resolveNeverExpires s.Clock s.Capabilities hash
          | _ -> resolve s.Clock s.Capabilities hash
        let resolution = match resolved with Ok r -> Resolution.AsToken r | Error e -> Resolution.Refused e
        { s with Presents = s.Presents @ [ { AtStep = s.Step; At = s.Clock; Token = Some token; Resolution = resolution; LastAcceptedBefore = Map.tryFind token s.LastAccepted } ] }
    | SimEvent.PresentUnknown n ->
      let hash = TokenHash.ofToken (Token.ofEntropy (entropyOf (1000 + n)))
      let resolution =
        match resolve s.Clock s.Capabilities hash, behavior with
        | Ok r, _ -> Resolution.AsToken r
        | Error _, Behavior.BadTokenFallsBackTwin -> Resolution.AsConnection
        | Error e, _ -> Resolution.Refused e
      { s with Presents = s.Presents @ [ { AtStep = s.Step; At = s.Clock; Token = None; Resolution = resolution; LastAcceptedBefore = None } ] }
    | SimEvent.Claim(token, scope) ->
      match Map.tryFind token s.RawTokens with
      | None -> s
      | Some raw ->
        match resolve s.Clock s.Capabilities (TokenHash.ofToken raw) with
        | Error _ -> s
        | Ok record ->
          let allowed =
            match behavior with
            | Behavior.NaivePrefixClaimTwin ->
              // The check a scope written as a plain string prefix would do.
              let rawPath =
                match scope with
                | ClaimScope.File p -> p
                | ClaimScope.Project p -> p
              let prefix = ScopePrefix.value record.Grant.Scope
              prefix = "" || rawPath = prefix || rawPath.StartsWith(prefix + "/", StringComparison.Ordinal)
            | Behavior.Real
            | Behavior.MintsFromLedgerEntropyTwin
            | Behavior.MintIgnoresMinterTwin
            | Behavior.MintClampsTwin
            | Behavior.TouchRevivesTwin
            | Behavior.MintOverwritesTwin
            | Behavior.NeverExpiresTwin
            | Behavior.BadTokenFallsBackTwin -> Result.isOk (admitClaim record.Grant scope)
          { s with Claims = s.Claims @ [ { AtStep = s.Step; Token = token; Scope = scope; Grant = record.Grant; Allowed = allowed } ] }

  type Scenario = { Seed: int; Events: SimEvent list }

  let trace (behavior: Behavior) (scenario: Scenario) : State list =
    scenario.Events |> List.scan (step behavior) initial

  // ── Scenario generation ───────────────────────────────────────────────────

  let private prefixPool = [| ""; "src"; "src/Foo"; "src/Foo/Sub"; "src/Bar" |]

  let private claimPool =
    [| ClaimScope.File "src/Foo/a.fs"
       ClaimScope.File "src/Bar/b.fs"
       ClaimScope.File "src/Foo/../Bar/b.fs"
       ClaimScope.File "src/Foo/Sub/../c.fs"
       ClaimScope.File "src/FooBar/d.fs"
       ClaimScope.Project "src/Foo/Foo.fsproj"
       ClaimScope.Project "src/Foo/../Bar/Bar.fsproj"
       ClaimScope.File "../escape.fs"
       ClaimScope.File "top.fs" |]

  let private tokenCount = 6

  let private mintOfSeed (rng: Random) (token: int) (minter: MinterKind) : SimEvent =
    let scope = match ScopePrefix.tryParse prefixPool.[rng.Next prefixPool.Length] with Ok p -> p | Error _ -> ScopePrefix.repoRoot
    // Zero to 10 hours: zero is already expired, some are past the 8 hour maximum, some beyond the narrow conductor's own 2 hours.
    SimEvent.Mint(token, minter, RolePreset.all.[rng.Next RolePreset.all.Length], scope, rng.Next 600)

  /// A pure function of `seed`.
  let scenarioOf (seed: int) : Scenario =
    let rng = Random seed
    let n = 25 + rng.Next 40
    let minters = [| MinterKind.ConductorTop; MinterKind.ConductorTop; MinterKind.ConductorTop; MinterKind.ConductorNarrow; MinterKind.ConductorNarrow; MinterKind.PlainMember; MinterKind.Anonymous |]
    let events =
      [ for _ in 1 .. n ->
          let token = rng.Next tokenCount
          match rng.Next 12 with
          | 0 | 1 | 2 -> mintOfSeed rng token minters.[rng.Next minters.Length]
          | 3 | 4 -> SimEvent.Present token
          | 5 -> SimEvent.PresentUnknown(rng.Next 3)
          | 6 -> SimEvent.Touch token
          | 7 -> SimEvent.Revoke(token, rng.Next 5 <> 0)
          | 8 | 9 -> SimEvent.Claim(token, claimPool.[rng.Next claimPool.Length])
          | _ -> SimEvent.PassMinutes(1 + rng.Next 90) ]
    { Seed = seed; Events = events }
