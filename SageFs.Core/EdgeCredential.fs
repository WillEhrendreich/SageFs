namespace SageFs

open System
open SageFs.Capability

/// What the daemon's HTTP edge makes of the `Authorization` header, and whether a route accepts it.
///
/// Slice 0 of docs/plans/a-token-for-every-daemon-call.md. Two kinds of credential exist: the operator's
/// (the person and their launcher: `sfo_` plus 43 base64url characters, kept in `operator.token`) and a
/// member token (`sfm_`, what `mint_member` hands out). A route is either `Open`, as every route is today,
/// or `OperatorOnly`. The decision is pure: the edge hands it the header and the hash it holds, and turns
/// the answer into a status and a body.
///
/// A refusal for a credential that was PRESENTED and cannot be used is a 403, the same status #182 uses for
/// a token the daemon cannot resolve, because it is the one that makes a client's header helper run again.
/// A refusal for no credential at all is a 401.
module EdgeCredential =

  /// The operator credential's text: shape, and the one place a candidate is judged well formed.
  module OperatorCredential =
    /// Every operator credential starts with this, so a leaked one is recognizable in a log or a scanner.
    let prefix = "sfo_"

    /// How many random bytes a credential is made of.
    let entropyBytes : int = 32

    /// 32 bytes in base64url without padding.
    let encodedLength : int = 43

    /// The credential text for `entropy`. Pure: the edge draws the bytes from a cryptographic source.
    let ofEntropy (entropy: byte[]) : string =
      prefix + Convert.ToBase64String(entropy).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    let isBase64Url (c: char) : bool =
      Char.IsAsciiLetterOrDigit c || c = '-' || c = '_'

    /// Whether `raw` is exactly the prefix and `encodedLength` base64url characters.
    let isWellFormed (raw: string) : bool =
      match isNull raw with
      | true -> false
      | false ->
        raw.StartsWith(prefix, StringComparison.Ordinal)
        && raw.Length = prefix.Length + encodedLength
        && raw.Substring(prefix.Length) |> Seq.forall isBase64Url

  /// SHA-256 of an operator credential, lowercase hex. The only form of the credential the daemon keeps in
  /// memory. Opaque: only `ofCredential` and `tryOfHex` make one.
  type OperatorHash = private OperatorHash of string

  module OperatorHash =
    let ofCredential (credential: string) : OperatorHash =
      let digest = Security.Cryptography.SHA256.HashData(Text.Encoding.UTF8.GetBytes credential)
      OperatorHash(digest |> Array.map (fun b -> b.ToString "x2") |> String.concat "")

    let value (OperatorHash hex) : string = hex

  /// Why an `Authorization` header could not be read as a credential.
  [<RequireQualifiedAccess>]
  type Unreadability =
    /// The scheme is not `Bearer`.
    | NotBearer
    /// `Bearer` with nothing after it.
    | EmptyCredential
    /// The credential starts with neither `sfo_` nor `sfm_`, or is far longer than any credential.
    | UnknownPrefix
    /// It starts with `sfo_` and is not exactly the shape of an operator credential.
    | MalformedOperator

  /// What a request presented. There is no case that carries a raw credential.
  [<RequireQualifiedAccess>]
  type Presented =
    | Nothing
    | Operator of OperatorHash
    | Member of TokenHash
    | Unreadable of Unreadability

  module Presented =
    let bearerScheme = "Bearer"

    /// What the `Authorization` header value presents. A header that is there and cannot be read is
    /// `Unreadable` with the reason, never `Nothing`: a malformed credential must be refused, not ignored.
    let ofAuthorization (header: string option) : Presented =
      match header with
      | None -> Presented.Nothing
      | Some value ->
        match value.Trim() with
        | trimmed when trimmed.Length < bearerScheme.Length || not (trimmed.StartsWith(bearerScheme, StringComparison.OrdinalIgnoreCase)) ->
          Presented.Unreadable Unreadability.NotBearer
        | trimmed when trimmed.Length > bearerScheme.Length && not (Char.IsWhiteSpace trimmed.[bearerScheme.Length]) ->
          Presented.Unreadable Unreadability.NotBearer
        | trimmed ->
          let credential = trimmed.Substring(bearerScheme.Length).Trim()
          match credential with
          | "" -> Presented.Unreadable Unreadability.EmptyCredential
          | raw when raw.Length > CapabilityTransport.maxTokenLength -> Presented.Unreadable Unreadability.UnknownPrefix
          | raw when raw.StartsWith(OperatorCredential.prefix, StringComparison.Ordinal) ->
            match OperatorCredential.isWellFormed raw with
            | true -> Presented.Operator(OperatorHash.ofCredential raw)
            | false -> Presented.Unreadable Unreadability.MalformedOperator
          | raw when raw.StartsWith(Token.prefix, StringComparison.Ordinal) -> Presented.Member(TokenHash.ofToken raw)
          | _ -> Presented.Unreadable Unreadability.UnknownPrefix

  /// What a route needs. Every route is `Open` today; a route is moved to `OperatorOnly` one at a time,
  /// and a ratchet counts the `Open` ones.
  [<RequireQualifiedAccess>]
  type EdgeAccess =
    | Open
    | OperatorOnly

  /// Who the edge recognized the caller as.
  [<RequireQualifiedAccess>]
  type Principal =
    | Anonymous
    | Operator
    | Member of TokenHash

  [<RequireQualifiedAccess>]
  type EdgeRefusal =
    /// The route needs the operator and the request carried no credential.
    | NoCredential
    /// A member token reached an operator-only route. A member token is never an operator.
    | MemberNotAllowed
    /// An operator-shaped credential that is not the daemon's own (an old one, or another daemon's).
    | OperatorNotRecognized
    /// A header was present and could not be read.
    | UnreadableCredential of Unreadability

  /// Whether `presented` may use a route that needs `access`, given the hash of the daemon's own operator
  /// credential. Total: every presentation and every access has an answer. An `Open` route admits
  /// everything and says who the caller was recognized as; an `OperatorOnly` route admits exactly the
  /// daemon's own hash.
  let decide (expected: OperatorHash) (presented: Presented) (access: EdgeAccess) : Result<Principal, EdgeRefusal> =
    match access with
    | EdgeAccess.Open ->
      match presented with
      | Presented.Operator hash when hash = expected -> Ok Principal.Operator
      | Presented.Member hash -> Ok(Principal.Member hash)
      | Presented.Operator _
      | Presented.Nothing
      | Presented.Unreadable _ -> Ok Principal.Anonymous
    | EdgeAccess.OperatorOnly ->
      match presented with
      | Presented.Operator hash when hash = expected -> Ok Principal.Operator
      | Presented.Operator _ -> Error EdgeRefusal.OperatorNotRecognized
      | Presented.Member _ -> Error EdgeRefusal.MemberNotAllowed
      | Presented.Nothing -> Error EdgeRefusal.NoCredential
      | Presented.Unreadable why -> Error(EdgeRefusal.UnreadableCredential why)

  /// The two statuses a refusal takes. The edge turns each into its number.
  [<RequireQualifiedAccess>]
  type EdgeStatus =
    | Unauthorized
    | Forbidden

  module EdgeRefusal =
    /// No credential is a 401: nothing was presented, so there is nothing to refresh. A credential that was
    /// presented and cannot be used is a 403, as for a member token the daemon cannot resolve (#182).
    let statusOf (refusal: EdgeRefusal) : EdgeStatus =
      match refusal with
      | EdgeRefusal.NoCredential -> EdgeStatus.Unauthorized
      | EdgeRefusal.MemberNotAllowed
      | EdgeRefusal.OperatorNotRecognized
      | EdgeRefusal.UnreadableCredential _ -> EdgeStatus.Forbidden

    /// The `error` field of a refusal body: OAuth's own names where they fit (RFC 6750), so a client that reads the
    /// vocabulary reads it here.
    let errorCode (refusal: EdgeRefusal) : string =
      match refusal with
      | EdgeRefusal.NoCredential -> "unauthorized"
      | EdgeRefusal.MemberNotAllowed -> "insufficient_scope"
      | EdgeRefusal.OperatorNotRecognized
      | EdgeRefusal.UnreadableCredential _ -> "invalid_token"

    let unreadableName (why: Unreadability) : string =
      match why with
      | Unreadability.NotBearer -> "not_bearer"
      | Unreadability.EmptyCredential -> "empty_credential"
      | Unreadability.UnknownPrefix -> "unknown_prefix"
      | Unreadability.MalformedOperator -> "malformed_operator"

    /// The `case` field of the refusal body, one name per refusal.
    let caseName (refusal: EdgeRefusal) : string =
      match refusal with
      | EdgeRefusal.NoCredential -> "no_credential"
      | EdgeRefusal.MemberNotAllowed -> "member_not_allowed"
      | EdgeRefusal.OperatorNotRecognized -> "operator_not_recognized"
      | EdgeRefusal.UnreadableCredential why -> sprintf "unreadable_%s" (unreadableName why)

    /// What happened, in a sentence that names no credential.
    let message (refusal: EdgeRefusal) : string =
      match refusal with
      | EdgeRefusal.NoCredential -> "This route needs the operator credential and the request carried none."
      | EdgeRefusal.MemberNotAllowed -> "This route is for the operator. A member token cannot use it, whatever its role."
      | EdgeRefusal.OperatorNotRecognized -> "The operator credential in the request is not this daemon's. It may be from before operator.token was replaced, or from another daemon."
      | EdgeRefusal.UnreadableCredential Unreadability.NotBearer -> "The Authorization header is not a Bearer credential."
      | EdgeRefusal.UnreadableCredential Unreadability.EmptyCredential -> "The Authorization header says Bearer and carries no credential. An environment variable that was never set is the usual cause."
      | EdgeRefusal.UnreadableCredential Unreadability.UnknownPrefix -> "The credential in the Authorization header starts with neither sfo_ nor sfm_, or is far longer than any credential."
      | EdgeRefusal.UnreadableCredential Unreadability.MalformedOperator -> "The credential starts with sfo_ and is not the shape of an operator credential. It is probably cut short."

    /// What the caller should do next.
    let suggestedAction (refusal: EdgeRefusal) : string =
      match refusal with
      | EdgeRefusal.NoCredential
      | EdgeRefusal.UnreadableCredential _
      | EdgeRefusal.OperatorNotRecognized ->
        "Send the contents of operator.token from the daemon's data directory as 'Authorization: Bearer <credential>', or run 'sagefs member mint' on the machine the daemon runs on."
      | EdgeRefusal.MemberNotAllowed ->
        "Ask the person running the daemon to mint the token with 'sagefs member mint', which reads operator.token."

  /// What the edge writes on a request's principal about the operator credential, for the MCP request filters to
  /// read. A request that presented none carries no claim.
  [<RequireQualifiedAccess>]
  type OperatorClaim =
    | Recognized
    | Refused

  module OperatorClaim =
    /// The claim that holds the kind. The credential itself goes no further than the edge.
    let claimType = "urn:sagefs:operator-credential"

    /// The claim that holds the sentence naming why a credential was refused, for the one tool a refused caller is shown.
    let refusalClaimType = "urn:sagefs:operator-credential-refusal"

    let all : OperatorClaim list = [ OperatorClaim.Recognized; OperatorClaim.Refused ]

    let toValue (claim: OperatorClaim) : string =
      match claim with
      | OperatorClaim.Recognized -> "recognized"
      | OperatorClaim.Refused -> "refused"

    let tryOfValue (value: string) : OperatorClaim option =
      all |> List.tryFind (fun claim -> toValue claim = value)

  /// What a request's `Authorization` header says about the operator door. Only a credential that is shaped like an
  /// operator's can be refused here: a header that is another scheme, another system's bearer token or a member token
  /// belongs to someone else, and a refusal would break a client that sends it for its own reasons.
  [<RequireQualifiedAccess>]
  type OperatorPresentation =
    | NotPresented
    | Recognized
    | Refused of EdgeRefusal

  module OperatorPresentation =
    let ofAuthorization (expected: OperatorHash) (header: string option) : OperatorPresentation =
      match Presented.ofAuthorization header with
      | Presented.Operator hash when hash = expected -> OperatorPresentation.Recognized
      | Presented.Operator _ -> OperatorPresentation.Refused EdgeRefusal.OperatorNotRecognized
      | Presented.Unreadable Unreadability.MalformedOperator ->
        OperatorPresentation.Refused(EdgeRefusal.UnreadableCredential Unreadability.MalformedOperator)
      | Presented.Nothing
      | Presented.Member _
      | Presented.Unreadable Unreadability.NotBearer
      | Presented.Unreadable Unreadability.EmptyCredential
      | Presented.Unreadable Unreadability.UnknownPrefix -> OperatorPresentation.NotPresented

    let claimOf (presentation: OperatorPresentation) : OperatorClaim option =
      match presentation with
      | OperatorPresentation.NotPresented -> None
      | OperatorPresentation.Recognized -> Some OperatorClaim.Recognized
      | OperatorPresentation.Refused _ -> Some OperatorClaim.Refused
