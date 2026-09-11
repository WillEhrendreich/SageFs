namespace SageFs.Server

open System
open System.Net

/// Origin/CSRF gate for the daemon's HTTP surfaces (MCP :37749, dashboard
/// :37750) and — through WorkerHttpTransport — the worker's.
///
/// THREAT MODEL
/// Anything that makes the daemon or a worker evaluate F# runs code as the
/// logged-in user. The listeners bind loopback only (SageFsConfig.BindHost has
/// no non-loopback case), so the network cannot connect at all. Two kinds of
/// caller can:
///   1. Local processes — editors, the CLI, MCP agents, curl. They already run
///      as the user, so they are inside the trust boundary. They send no
///      browser signals (no Origin, no Sec-Fetch-*) and pass the origin checks.
///   2. Browser pages. The browser connects to loopback on behalf of ANY page
///      the user has open: a remote site, a DNS-rebinding page whose hostname
///      resolves to 127.0.0.1, or — the case a loopback-prefix gate misses — a
///      page served from ANOTHER localhost port: an npm dev server running a
///      compromised dependency, a Jupyter notebook, the user's own WebLive app.
///      Every localhost port is the SAME SITE, so `Sec-Fetch-Site: same-site`
///      and a loopback Origin prove nothing about who is asking.
///
/// POLICY (fail closed on every browser signal)
///   - Host must name the loopback interface (localhost, 127.0.0.0/8, ::1),
///     parsed as a URI authority (IPv6 brackets, no userinfo, no path). Any
///     other Host is DNS rebinding or a proxy: reject.
///   - An Origin, when present, must EXACTLY equal one of the daemon's own
///     origins — http://{localhost|127.0.0.1|[::1]}:{each listener port} —
///     compared as parsed scheme + host + port, never by prefix. `null` and
///     unparseable origins are foreign.
///   - Sec-Fetch-Site same-site / cross-site with no Origin of our own: an
///     unsafe method is rejected; a safe method (GET/HEAD/OPTIONS) is allowed
///     for same-site (the browser's same-origin policy keeps a foreign page
///     from reading the response) and rejected for cross-site. Unknown values
///     are rejected. (The dashboard page POSTing to the MCP port is same-site
///     AND carries an own Origin, so it passes.)
///   - An unsafe method that carries a body must be Content-Type:
///     application/json. text/plain, form-urlencoded and multipart are the
///     CORS "simple" types a page may send cross-origin WITHOUT a preflight;
///     requiring JSON forces a preflight, whose foreign Origin this gate
///     rejects, so the real request is never sent. This also covers browsers
///     that omit Origin / Sec-Fetch-* entirely — and it applies to every
///     caller, so non-browser clients must label JSON bodies as JSON.
[<RequireQualifiedAccess>]
module HttpOriginGuard =

  /// A parsed web origin. Host is lower-case and keeps IPv6 brackets ("[::1]").
  type Origin = { Scheme: string; Host: string; Port: int }

  /// Parse an Origin header value. Only "scheme://host[:port]" is an origin —
  /// no userinfo, path, query or fragment; "null" is not an origin.
  let parseOrigin (raw: string) : Result<Origin, string> =
    let trimmed = raw.Trim()
    match Uri.TryCreate(trimmed, UriKind.Absolute) with
    | true, uri ->
      match uri.Scheme with
      | "http" | "https"
          when uri.UserInfo = "" && uri.PathAndQuery = "/" && uri.Fragment = ""
               && not (trimmed.EndsWith "/") ->
        Ok { Scheme = uri.Scheme; Host = uri.Host.ToLowerInvariant(); Port = uri.Port }
      | _ -> Error (sprintf "not a web origin: %s" raw)
    | _ -> Error (sprintf "not a web origin: %s" raw)

  /// The host part of a Host header value, parsed as a URI authority:
  /// "[::1]:37749" -> "[::1]", "LOCALHOST:37749" -> "localhost".
  let private hostOfAuthority (authority: string) : Result<string, string> =
    let a = authority.Trim()
    match a with
    | "" -> Error "empty Host"
    | _ ->
      match Uri.TryCreate("http://" + a + "/", UriKind.Absolute) with
      | true, uri when uri.UserInfo = "" && uri.PathAndQuery = "/" && uri.Fragment = "" ->
        Ok (uri.Host.ToLowerInvariant())
      | _ ->
        // An unbracketed IPv6 literal ("::1") is not a URI authority.
        match a.Contains ':', a.Contains '[', IPAddress.TryParse a with
        | true, false, (true, ip) -> Ok (sprintf "[%s]" (ip.ToString()))
        | _ -> Error (sprintf "unparseable Host %s" authority)

  /// A URI host names the loopback interface: "localhost" exactly, or a
  /// loopback IP literal. "localhost.evil.com" is a DNS name, not loopback.
  let private isLoopbackName (host: string) : bool =
    match host with
    | "localhost" -> true
    | h ->
      match IPAddress.TryParse(h.Trim('[', ']')) with
      | true, ip -> IPAddress.IsLoopback ip
      | _ -> false

  /// True when a Host header value (with or without port) names loopback.
  let isLoopbackHost (hostHeader: string) : bool =
    match hostOfAuthority hostHeader with
    | Ok host -> isLoopbackName host
    | Error _ -> false

  /// True when an Origin header value is an origin on the loopback interface
  /// (any port). Only the worker's DevReload stream accepts such an origin —
  /// it is not a sufficient credential for anything that executes code.
  let isLoopbackOrigin (origin: string) : bool =
    match parseOrigin origin with
    | Ok o -> isLoopbackName o.Host
    | Error _ -> false

  /// The exact origins a server's own pages are served from.
  type OwnOrigins = private { Origins: Set<Origin> }

  [<RequireQualifiedAccess>]
  module OwnOrigins =

    /// The loopback host forms a browser can use to reach a loopback listener.
    let loopbackHostForms = [ "localhost"; "127.0.0.1"; "[::1]" ]

    /// Every loopback origin of the given listener ports.
    let ofPorts (ports: int list) : OwnOrigins =
      { Origins =
          set [ for port in ports do
                  for host in loopbackHostForms ->
                    { Scheme = "http"; Host = host; Port = port } ] }

    let contains (own: OwnOrigins) (origin: Origin) : bool =
      own.Origins.Contains origin

    let toList (own: OwnOrigins) : Origin list =
      own.Origins |> Set.toList

  /// Whether the request carries a body.
  [<RequireQualifiedAccess>]
  type Body =
    | Empty
    | Present

  /// The parts of a request the gate decides on. Header options are None when
  /// the header is absent.
  type Request = {
    Method: string
    Host: string option
    SecFetchSite: string option
    Origin: string option
    ContentType: string option
    Body: Body
  }

  [<RequireQualifiedAccess>]
  type Rejection =
    /// Host is not the loopback interface — DNS rebinding or a proxy.
    | ForeignHost of host: string
    /// Origin is not one of this server's own origins.
    | ForeignOrigin of origin: string
    /// The browser flagged the request same-site / cross-site and it carries
    /// no origin of our own.
    | CrossSite of fetchSite: string
    /// A Sec-Fetch-Site value this gate does not know.
    | UnknownFetchSite of fetchSite: string
    /// An unsafe request whose body is not labelled application/json.
    | NonJsonBody of contentType: string

  [<RequireQualifiedAccess>]
  module Rejection =

    let describe (rejection: Rejection) : string =
      match rejection with
      | Rejection.ForeignHost h ->
        sprintf "Host %s is not the loopback interface" h
      | Rejection.ForeignOrigin o ->
        sprintf "Origin %s is not one of this server's own origins" o
      | Rejection.CrossSite s ->
        sprintf "Sec-Fetch-Site %s without an origin of this server" s
      | Rejection.UnknownFetchSite s ->
        sprintf "unrecognized Sec-Fetch-Site %s" s
      | Rejection.NonJsonBody ct ->
        sprintf "request body Content-Type %s is not application/json — send JSON with Content-Type: application/json" ct

    /// 415 for a wrongly labelled body; 403 for everything browser-shaped.
    let statusCode (rejection: Rejection) : int =
      match rejection with
      | Rejection.NonJsonBody _ -> 415
      | Rejection.ForeignHost _
      | Rejection.ForeignOrigin _
      | Rejection.CrossSite _
      | Rejection.UnknownFetchSite _ -> 403

  type Verdict =
    | Allow
    | Reject of Rejection

  [<RequireQualifiedAccess>]
  type MethodKind =
    /// GET/HEAD/OPTIONS — no side effects by HTTP contract.
    | Safe
    | Unsafe

  let methodKind (httpMethod: string) : MethodKind =
    match httpMethod.Trim().ToUpperInvariant() with
    | "GET" | "HEAD" | "OPTIONS" -> MethodKind.Safe
    | _ -> MethodKind.Unsafe

  /// Media type application/json (parameters such as charset allowed).
  let isJsonContentType (contentType: string) : bool =
    contentType.Split(';').[0].Trim().ToLowerInvariant() = "application/json"

  [<RequireQualifiedAccess>]
  type private OriginClaim =
    | Absent
    | Own
    | Foreign of raw: string

  [<RequireQualifiedAccess>]
  type private FetchSite =
    | Absent
    | SameOrigin
    | UserInitiated
    | SameSite of raw: string
    | CrossSite of raw: string
    | Unknown of raw: string

  let private fetchSiteOf (value: string option) : FetchSite =
    match value with
    | None -> FetchSite.Absent
    | Some raw ->
      match raw.Trim().ToLowerInvariant() with
      | "same-origin" -> FetchSite.SameOrigin
      | "none" -> FetchSite.UserInitiated
      | "same-site" -> FetchSite.SameSite raw
      | "cross-site" -> FetchSite.CrossSite raw
      | _ -> FetchSite.Unknown raw

  /// The gate decision for one request against the server's own origins.
  let decide (own: OwnOrigins) (request: Request) : Verdict =
    match request.Host with
    | Some h when not (isLoopbackHost h) -> Reject (Rejection.ForeignHost h)
    | _ ->
    let claim =
      match request.Origin with
      | None -> OriginClaim.Absent
      | Some raw ->
        match parseOrigin raw with
        | Ok o when OwnOrigins.contains own o -> OriginClaim.Own
        | _ -> OriginClaim.Foreign raw
    let kind = methodKind request.Method
    let browserVerdict =
      match claim, fetchSiteOf request.SecFetchSite, kind with
      | OriginClaim.Foreign raw, _, _ -> Reject (Rejection.ForeignOrigin raw)
      | _, FetchSite.Unknown raw, _ -> Reject (Rejection.UnknownFetchSite raw)
      | OriginClaim.Absent, FetchSite.SameSite raw, MethodKind.Unsafe
      | OriginClaim.Absent, FetchSite.CrossSite raw, _ -> Reject (Rejection.CrossSite raw)
      | OriginClaim.Absent, FetchSite.SameSite _, MethodKind.Safe
      | OriginClaim.Absent, (FetchSite.Absent | FetchSite.SameOrigin | FetchSite.UserInitiated), _
      | OriginClaim.Own, _, _ -> Allow
    match browserVerdict, kind, request.Body with
    | Reject r, _, _ -> Reject r
    | Allow, MethodKind.Unsafe, Body.Present ->
      match request.ContentType with
      | Some ct when isJsonContentType ct -> Allow
      | Some ct -> Reject (Rejection.NonJsonBody ct)
      | None -> Reject (Rejection.NonJsonBody "(none)")
    | Allow, _, _ -> Allow
