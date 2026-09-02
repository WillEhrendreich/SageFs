namespace SageFs.Server

/// Origin/CSRF gate for the loopback HTTP surfaces (dashboard + MCP).
///
/// The threat: a malicious webpage (or DNS rebinding) POSTing cross-origin
/// to http://localhost:<port> mutating endpoints that create sessions at
/// arbitrary paths and evaluate arbitrary F# as the logged-in user.
///
/// Policy (fail closed on browser signals, open to local tooling):
///   - Sec-Fetch-Site: cross-site -> reject always. same-origin / none /
///     absent -> continue. (Browsers send Sec-Fetch-Site on every fetch;
///     a DNS-rebinding attack sends cross-site.)
///   - Origin header present and not loopback: reject (a rebinding or
///     cross-origin page sends the attacker's origin). Loopback origins
///     (http://localhost:<port> / http://127.0.0.1:<port>) pass.
///   - Neither header: curl, editors, MCP, CLI — pass. Binding is already
///     loopback-only by default, so no-header requests are local processes.
[<RequireQualifiedAccess>]
module HttpOriginGuard =

  type Verdict =
    | Allow
    | Reject of reason: string

  /// Normalize a Host header value (strip port, lowercase).
  let private hostName (host: string) : string =
    let h = host.Trim().ToLowerInvariant()
    match h.IndexOf(':') with
    | -1 -> h
    | i -> h.Substring(0, i)

  /// True when the host header names the loopback interface.
  let isLoopbackHost (host: string) : bool =
    match hostName host with
    | "localhost" | "127.0.0.1" | "::1" -> true
    | _ -> false

  /// True when an Origin header value is a loopback origin.
  let isLoopbackOrigin (origin: string) : bool =
    let o = origin.Trim().ToLowerInvariant()
    let isLoopbackSchemeHost =
      o.StartsWith("http://localhost")
      || o.StartsWith("http://127.0.0.1")
      || o.StartsWith("https://localhost")
      || o.StartsWith("https://127.0.0.1")
      || o.StartsWith("http://[::1]")
      || o.StartsWith("https://[::1]")
    // "null" Origin is sent by sandboxed iframes and some redirects — not
    // a real browser page origin; reject it (fail closed).
    isLoopbackSchemeHost && o <> "null"

  /// The gate decision for one request.
  ///
  /// hostHeader: the request Host (may be null/empty on HTTP/1.0 or odd
  /// clients). secFetchSite: the Sec-Fetch-Site header value (null when
  /// absent). origin: the Origin header value (null when absent).
  let decide
    (hostHeader: string option)
    (secFetchSite: string option)
    (origin: string option)
    : Verdict =
    // A Host header that is present but not loopback means the request
    // reached us via a different host name — DNS rebinding or a proxy.
    // Fail closed. (Absent Host is tolerated: some local clients omit it.)
    match hostHeader with
    | Some h when not (isLoopbackHost h) ->
      Reject (sprintf "non-loopback Host %s" h)
    | _ ->
      match secFetchSite with
      | Some site when site <> "same-origin" && site <> "none" && site <> "same-site" ->
        Reject (sprintf "cross-site Sec-Fetch-Site %s" site)
      | _ ->
        match origin with
        | Some o when not (isLoopbackOrigin o) ->
          Reject (sprintf "non-loopback Origin %s" o)
        | _ -> Allow
