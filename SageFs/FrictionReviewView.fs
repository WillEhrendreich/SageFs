module SageFs.Features.FrictionReviewView

/// Pure view-model for the dashboard friction review drawer. Converts the
/// in-memory FrictionReport + local send history into the exact shape the
/// drawer renders and the send handler POSTs — kept free of IO and DOM so
/// the contract is unit-testable.
///
/// Privacy boundary: this view is built from the LOCAL SQLite store only.
/// It never reads remote reports; the owner reads those through the
/// receiver's owner-gated endpoints, outside the dashboard.

open SageFs.Features.FrictionTelemetry
open SageFs.Features.FrictionTelemetryTypes
open SageFs.Features.FrictionSanitize
open SageFs.Features.FrictionSqlite

type FrictionReviewSnapshot = {
  /// The canonical in-memory report (source of truth for re-deriving the
  /// outgoing payload after the user edits reasons).
  Report: FrictionReport
  /// The sanitized outbound report (what the user reviews before sending).
  Outgoing: OutgoingReport
  /// Raw counts shown in the header.
  EventCount: int
  FeedbackCount: int
  /// Local send history (receipts recorded after successful sends).
  SentReports: SentReport list
  /// Whether the store is empty (nothing to review or send).
  IsEmpty: bool
}

/// Build the drawer view model from the canonical report + send history.
let build (report: FrictionReport) (sentReports: SentReport list) : FrictionReviewSnapshot =
  {
    Report = report
    Outgoing = toOutgoing (SageFsVersion.current ()) report Map.empty
    EventCount = report.TotalEvents
    FeedbackCount = report.TotalFeedbackItems
    SentReports = sentReports |> List.sortByDescending (fun s -> s.SentAtUtc)
    IsEmpty = report.TotalEvents = 0 && report.TotalFeedbackItems = 0
  }

/// Re-derive the outgoing payload with user edits applied per (tool, kind).
/// `edits` maps (tool, kind) -> edited reason text; toOutgoing sanitizes
/// each edited value so a user cannot push raw secrets out.
let withEdits
  (edits: Map<string * string, string>)
  (snap: FrictionReviewSnapshot)
  : FrictionReviewSnapshot =
  { snap with Outgoing = toOutgoing (SageFsVersion.current ()) snap.Report edits }

/// The default edit map the drawer binds its textareas to (tool, kind ->
/// latest raw reason).
let defaultEdits (report: FrictionReport) : Map<string * string, string> =
  report.RecentFeedback
  |> List.map (fun f ->
    (FrictionTelemetryTypes.ToolName.value f.Tool, string f.Kind),
    f.LatestReason)
  |> Map.ofList

/// Parse a client-supplied edits JSON object (keys "tool|kind") into the
/// (tool, kind) map. Unknown/malformed keys are dropped; values are
/// sanitized by toOutgoing on the server, so a client can never push raw
/// secrets out through an edit.
let parseEditsJson (json: string) : Map<string * string, string> =
  if System.String.IsNullOrWhiteSpace json then Map.empty
  else
    try
      use doc = System.Text.Json.JsonDocument.Parse(json)
      match doc.RootElement.ValueKind with
      | System.Text.Json.JsonValueKind.Object ->
        let mutable acc = Map.empty
        for prop in doc.RootElement.EnumerateObject() do
          let key = prop.Name
          let sepIndex = key.IndexOf '|'
          if sepIndex > 0 && sepIndex < key.Length - 1 then
            let tool = key.Substring(0, sepIndex)
            let kind = key.Substring(sepIndex + 1)
            match prop.Value.ValueKind with
            | System.Text.Json.JsonValueKind.String ->
              acc <- Map.add (tool, kind) (prop.Value.GetString()) acc
            | _ -> ()
        acc
      | _ -> Map.empty
    with _ -> Map.empty

/// One-shot: build the outgoing report for a send from the canonical report
/// + client-supplied edits JSON. Pure and sanitized — this is what the
/// server-authoritative send handler POSTs.
let buildOutgoingForSend (report: FrictionReport) (editsJson: string) : OutgoingReport =
  let edits = parseEditsJson editsJson
  toOutgoing (SageFsVersion.current ()) report edits
