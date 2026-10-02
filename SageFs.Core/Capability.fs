namespace SageFs

open System
open SageFs.Cohort
open SageFs.MemberTable

/// RED STUB. The types are final; every function throws until the GREEN commit.
module Capability =

  let private notImplemented () : 'a = raise (NotImplementedException "RED: Capability is not implemented yet")

  // ── Scope prefix ──────────────────────────────────────────────────────────

  /// A canonical repo-relative directory a grant is confined to. The empty prefix is the whole repo.
  type ScopePrefix = private ScopePrefix of string

  module ScopePrefix =
    let repoRoot : ScopePrefix = ScopePrefix ""
    let tryParse (raw: string) : Result<ScopePrefix, PathRefusal> = notImplemented ()
    let value (prefix: ScopePrefix) : string = notImplemented ()
    let isWithin (inner: ScopePrefix) (outer: ScopePrefix) : bool = notImplemented ()
    let covers (prefix: ScopePrefix) (scope: ClaimScope) : bool = notImplemented ()

  // ── Roles: closed presets, closed tool classes ────────────────────────────

  [<RequireQualifiedAccess>]
  type ToolClass =
    | CohortRead
    | CohortMembership
    | CohortWork
    | CohortAdmin
    | SessionRead
    | Feedback
    | CodeAnalysis
    | TestRun
    | Leases
    | Eval
    | SessionLifecycle
    | AppControl
    | Maintenance

  module ToolClass =
    let all : ToolClass list =
      [ ToolClass.CohortRead; ToolClass.CohortMembership; ToolClass.CohortWork; ToolClass.CohortAdmin
        ToolClass.SessionRead; ToolClass.Feedback; ToolClass.CodeAnalysis; ToolClass.TestRun; ToolClass.Leases
        ToolClass.Eval; ToolClass.SessionLifecycle; ToolClass.AppControl; ToolClass.Maintenance ]
    let toToken (toolClass: ToolClass) : string = notImplemented ()
    let ofTool (toolName: string) : ToolClass option = notImplemented ()
    let toolsOf (toolClass: ToolClass) : string list = notImplemented ()

  [<RequireQualifiedAccess>]
  type RolePreset =
    | Observer
    | Analysis
    | Verifier
    | Implementer

  module RolePreset =
    let all : RolePreset list = [ RolePreset.Observer; RolePreset.Analysis; RolePreset.Verifier; RolePreset.Implementer ]
    let toToken (preset: RolePreset) : string = notImplemented ()
    let tryParse (raw: string) : Result<RolePreset, string> = notImplemented ()
    let joinableRole (preset: RolePreset) : JoinableRole = notImplemented ()
    let toolClasses (preset: RolePreset) : Set<ToolClass> = notImplemented ()
    let isNarrowerOrEqual (a: RolePreset) (b: RolePreset) : bool = notImplemented ()

  // ── Grant ─────────────────────────────────────────────────────────────────

  type Grant = {
    Preset: RolePreset
    Scope: ScopePrefix
    NotAfter: DateTime
  }

  [<RequireQualifiedAccess>]
  type Widening =
    | Role of requested: RolePreset * minter: RolePreset
    | Scope of requested: ScopePrefix * minter: ScopePrefix
    | Expiry of requested: DateTime * minter: DateTime

  module Grant =
    /// What the conductor may hand out: everything, everywhere, for ever.
    let conductorAuthority : Grant = { Preset = RolePreset.Implementer; Scope = ScopePrefix.repoRoot; NotAfter = DateTime.MaxValue }
    let isNarrowerOrEqual (a: Grant) (b: Grant) : bool = notImplemented ()
    let wideningsOf (requested: Grant) (minter: Grant) : Widening list = notImplemented ()

  // ── Token: shown once, only its hash is kept ──────────────────────────────

  type TokenHash = private TokenHash of string

  module TokenHash =
    let ofToken (rawToken: string) : TokenHash = notImplemented ()
    let value (hash: TokenHash) : string = notImplemented ()

  module Token =
    let entropyBytes : int = 32
    let ofEntropy (entropy: byte[]) : string = notImplemented ()

  type CapabilityId = CapabilityId of string

  module CapabilityId =
    let ofHash (hash: TokenHash) : CapabilityId = notImplemented ()
    let value (CapabilityId id) : string = id
    let memberId (id: CapabilityId) : MemberId = notImplemented ()
    let tryOfMemberId (who: MemberId) : CapabilityId option = notImplemented ()

  // ── State, commands, the pure reducer ─────────────────────────────────────

  [<RequireQualifiedAccess>]
  type CapabilityStatus =
    | Active
    | Revoked of at: DateTime

  type CapabilityRecord = {
    Id: CapabilityId
    MintedBy: MemberId
    MintedAt: DateTime
    LastSeen: DateTime
    Grant: Grant
    Status: CapabilityStatus
  }

  /// The capability table. Keyed by the token's hash: the token itself is never stored.
  type CapabilityState = { Records: Map<TokenHash, CapabilityRecord> }

  module CapabilityState =
    let empty : CapabilityState = { Records = Map.empty }

  type Minter = { Authority: Authority<MemberId>; Grant: Grant }

  [<RequireQualifiedAccess>]
  type CapabilityCommand =
    | Mint of minter: Minter * requested: Grant * hash: TokenHash
    | Touch of hash: TokenHash
    | Revoke of by: Authority<MemberId> * target: CapabilityId

  [<RequireQualifiedAccess>]
  type CapabilityEvent =
    | Minted of CapabilityId * Grant
    | Touched of CapabilityId
    | Revoked of CapabilityId

  [<RequireQualifiedAccess>]
  type MintRefusal =
    | NotConductor
    | DuplicateToken
    | NotInTheFuture of notAfter: DateTime
    | WouldWiden of Widening list
    | LifetimeTooLong of requested: TimeSpan * max: TimeSpan

  [<RequireQualifiedAccess>]
  type RevokeRefusal =
    | NotConductor
    | UnknownCapability of CapabilityId
    | AlreadyRevoked of at: DateTime

  [<RequireQualifiedAccess>]
  type CapabilityRefusal =
    | Mint of MintRefusal
    | Revoke of RevokeRefusal

  [<RequireQualifiedAccess>]
  type PresentRefusal =
    | Unknown
    | Expired of notAfter: DateTime
    | LeaseLapsed of lastSeen: DateTime
    | Revoked of at: DateTime

  let decide (now: DateTime) (state: CapabilityState) (command: CapabilityCommand)
      : Result<CapabilityState * CapabilityEvent list, CapabilityRefusal> =
    notImplemented ()

  let resolve (now: DateTime) (state: CapabilityState) (hash: TokenHash) : Result<CapabilityRecord, PresentRefusal> =
    notImplemented ()

  // ── What a grant lets a call do ───────────────────────────────────────────

  [<RequireQualifiedAccess>]
  type ToolRefusal =
    | NotInRole of tool: string * toolClass: ToolClass * preset: RolePreset
    | Unclassified of tool: string

  [<RequireQualifiedAccess>]
  type ScopeRefusal =
    | OutsideScope of requested: ClaimScope * granted: ScopePrefix
    | MalformedPath of PathRefusal

  let admitTool (grant: Grant) (toolName: string) : Result<unit, ToolRefusal> = notImplemented ()

  let admitClaim (grant: Grant) (scope: ClaimScope) : Result<unit, ScopeRefusal> = notImplemented ()

  let visibleTools (grant: Grant) (registered: string list) : string list = notImplemented ()

  // ── Identity policy: what a token-less connection is ──────────────────────

  [<RequireQualifiedAccess>]
  type IdentityPolicy =
    /// A connection without a token is a plain member, as before tokens existed. The default.
    | ConnectionsAllowed
    /// A connection without a token may read status, and the conductor may act. Everyone else needs a token.
    | TokenRequired

  module IdentityPolicy =
    let all : IdentityPolicy list = [ IdentityPolicy.ConnectionsAllowed; IdentityPolicy.TokenRequired ]
    let toToken (policy: IdentityPolicy) : string = notImplemented ()
    let tryParse (raw: string) : Result<IdentityPolicy, string> = notImplemented ()
    let defaultPolicy : IdentityPolicy = IdentityPolicy.ConnectionsAllowed

  [<RequireQualifiedAccess>]
  type PolicyRefusal = TokenRequired of tool: string

  let admitTokenless (policy: IdentityPolicy) (authority: Authority<MemberId>) (toolName: string) : Result<unit, PolicyRefusal> =
    notImplemented ()
