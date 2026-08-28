module SageFs.Features.FrictionSanitize

/// Sanitization for friction reports before they leave the user's machine.
///
/// This is the FIRST line of defense — the Cloudflare Worker re-applies
/// the same patterns as defense-in-depth, so a bug here can't leak raw
/// paths into R2. But if this module is correct, the user never even
/// sees "what would be sent" with sensitive data in it.

open System.Text.RegularExpressions
open SageFs.Features.FrictionTelemetryTypes

/// Max length of a free-text field after sanitization. Matches the
/// server-side cap so the user's preview and the actual payload agree.
let MaxTextLen = 200
let MaxAltLen = 50
let MaxToolNameLen = 100
let MaxBlockerLen = 100
let MaxKindLen = 50

// Paths — non-greedy, stop at whitespace or common delimiters so
// surrounding text survives. The client is on Windows; the worker
// covers both for defense.
let private WindowsPathRegex =
  Regex(@"[A-Za-z]:[\\\/](?:[^\\\/*??""<>|\s]*[\\\/])*[^\\\/*??""<>|\s]*", RegexOptions.Compiled)
let private UncPathRegex =
  Regex(@"\\\\[^\s\\/:*?""<>|]+(?:\\[^\s\\/:*?""<>|]+)+", RegexOptions.Compiled)
let private UnixPathRegex =
  Regex(@"\/(?:home|Users|root|tmp|var|opt|etc|srv|mnt)\/[^\s'"",<>]*", RegexOptions.Compiled)
let private Ipv4Regex =
  Regex(@"\b(?:(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\b", RegexOptions.Compiled)
let private EmailRegex =
  Regex(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", RegexOptions.Compiled)
// 8+ char hex. Shorter could be timestamps or other benign numbers.
let private SessionIdRegex =
  Regex(@"\b[a-f0-9]{8,40}\b", RegexOptions.Compiled ||| RegexOptions.IgnoreCase)

let private redactPaths (s: string) : string =
  let s1 = WindowsPathRegex.Replace(s, fun m -> $"{m.Value.[0]}:\<path>")
  let s2 = UncPathRegex.Replace(s1, fun _ -> "\\\\<path>")
  UnixPathRegex.Replace(s2, fun _ -> "/<path>")

let private redactIps (s: string) : string = Ipv4Regex.Replace(s, "<ip>")
let private redactEmails (s: string) : string = EmailRegex.Replace(s, "<email>")
let private redactSessionIds (s: string) : string = SessionIdRegex.Replace(s, "<session-id>")

/// Apply all redactions, trim, and cap to maxLen.
let sanitizeText (s: string) (maxLen: int) : string =
  if System.String.IsNullOrEmpty s then ""
  else
    let scrubbed =
      s.Trim()
      |> redactPaths
      |> redactIps
      |> redactEmails
      |> redactSessionIds
    if scrubbed.Length <= maxLen then scrubbed
    else scrubbed.[..maxLen - 2] + "..."

/// Unwrap a private-case smart wrapper to its underlying string.
let private unwrap (tool: ToolName) : string = ToolName.value tool

/// The shape we POST to the Worker. Lighter than the full FrictionReport
/// (no Session field, no internal counts, no raw reasons).
type OutgoingReport = {
  SchemaVersion: int
  SageFsVersion: string
  SubmittedAtUtc: string
  TotalEvents: int
  TotalFeedbackItems: int
  ToolsWithFriction: OutgoingTool list
  TopBlockers: OutgoingBlocker list
  FrequentTransitions: OutgoingTransition list
  RecentFeedback: OutgoingFeedback list
  RecommendedWorkItems: OutgoingWorkItem list
}
and OutgoingTool = {
  Tool: string
  Invocations: int
  Blocked: int
  Abandoned: int
  ExplicitFeedback: int
  SuggestedFix: string
}
and OutgoingBlocker = {
  Blocker: string
  Count: int
  AffectedTools: string list
}
and OutgoingTransition = {
  From: string
  To: string
  Count: int
}
and OutgoingFeedback = {
  Tool: string
  Kind: string
  Count: int
  Reason: string
  Alternative: string option
}
and OutgoingWorkItem = {
  Title: string
  TargetTool: string option
  Reason: string
  SuggestedAction: string
}

let CurrentSchemaVersion = 1

/// Build the outgoing report from the in-memory FrictionReport.
/// `reasons` is a map of (tool, kind) -> user-edited reason text; if a
/// key is present, the user-edited text is used instead of the raw
/// `LatestReason` from the FrictionReport. This is the edit step in
/// the dashboard — the user can override each reason before sending.
let toOutgoing
  (sageFsVersion: string)
  (report: SageFs.Features.FrictionTelemetry.FrictionReport)
  (reasons: Map<string * string, string>)
  : OutgoingReport =
  let pickReason (tool: string) (kind: string) (fallback: string) =
    match Map.tryFind (tool, kind) reasons with
    | Some edited -> sanitizeText edited MaxTextLen
    | None -> sanitizeText fallback MaxTextLen
  let pickAlt (fallback: string option) =
    fallback |> Option.map (fun s -> sanitizeText s MaxAltLen)
  {
    SchemaVersion = CurrentSchemaVersion
    SageFsVersion = sanitizeText sageFsVersion 50
    SubmittedAtUtc = System.DateTimeOffset.UtcNow.ToString("O")
    TotalEvents = max 0 report.TotalEvents
    TotalFeedbackItems = max 0 report.TotalFeedbackItems
    ToolsWithFriction = report.HighestPriorityTools |> List.map (fun t ->
      {
        Tool = sanitizeText (unwrap t.Tool) MaxToolNameLen
        Invocations = max 0 t.TotalInvocations
        Blocked = max 0 t.BlockedCount
        Abandoned = max 0 t.AbandonedCount
        ExplicitFeedback = max 0 t.ExplicitFeedbackCount
        SuggestedFix = sanitizeText t.SuggestedFixTarget MaxTextLen
      })
    TopBlockers = report.TopBlockers |> List.map (fun b ->
      {
        Blocker = sanitizeText (string b.Blocker) MaxBlockerLen
        Count = max 0 b.Count
        AffectedTools = b.MostAffectedTools |> List.map (fun t -> sanitizeText (unwrap t) MaxToolNameLen)
      })
    FrequentTransitions = report.FrequentTransitions |> List.map (fun t ->
      {
        From = sanitizeText (unwrap t.FromTool) MaxToolNameLen
        To = sanitizeText (unwrap t.ToTool) MaxToolNameLen
        Count = max 0 t.Frequency
      })
    RecentFeedback = report.RecentFeedback |> List.map (fun f ->
      {
        Tool = sanitizeText (unwrap f.Tool) MaxToolNameLen
        Kind = sanitizeText (string f.Kind) MaxKindLen
        Count = max 0 f.Count
        Reason = pickReason (unwrap f.Tool) (string f.Kind) f.LatestReason
        Alternative = pickAlt f.LatestAlternative
      })
    RecommendedWorkItems = report.RecommendedWorkItems |> List.map (fun w ->
      {
        Title = sanitizeText w.Title MaxTextLen
        TargetTool = w.TargetTool |> Option.map (fun t -> sanitizeText (unwrap t) MaxToolNameLen)
        Reason = sanitizeText w.Reason MaxTextLen
        SuggestedAction = sanitizeText w.SuggestedAction MaxTextLen
      })
  }
