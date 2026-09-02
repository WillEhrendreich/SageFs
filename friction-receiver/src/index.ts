/**
 * SageFs Friction Report Receiver
 *
 * Endpoint: POST /  (Content-Type: application/json)
 *
 * Stores sanitized friction reports in R2 and optionally notifies a
 * Discord webhook. Designed to be cheap (free tier: 100k req/day,
 * 10GB R2) and privacy-preserving.
 *
 * Defense-in-depth sanitization is applied here in addition to the
 * client-side sanitization, so even if a user's daemon has a bug,
 * what lands in R2 is still safe to look at.
 */

export interface Env {
  FRICTION_BUCKET: R2Bucket;
  DISCORD_WEBHOOK_URL?: string;
  INGEST_TOKEN?: string;
  OWNER_TOKEN?: string;
  MAX_PAYLOAD_BYTES?: string;
}

/** Shape of an incoming friction report. Validated at the edge. */
interface IncomingFrictionReport {
  schemaVersion: number;
  sageFsVersion: string;
  submittedAtUtc: string;
  totalEvents: number;
  totalFeedbackItems: number;
  toolsWithFriction: ToolFriction[];
  topBlockers: BlockerSummary[];
  frequentTransitions: TransitionSummary[];
  recentFeedback: FeedbackSummary[];
  recommendedWorkItems: WorkItem[];
}

interface ToolFriction {
  tool: string;
  invocations: number;
  blocked: number;
  abandoned: number;
  explicitFeedback: number;
  suggestedFix: string;
}

interface BlockerSummary {
  blocker: string;
  count: number;
  affectedTools: string[];
}

interface TransitionSummary {
  from: string;
  to: string;
  count: number;
}

interface FeedbackSummary {
  tool: string;
  kind: string;
  count: number;
  reason: string;
  alternative: string | null;
}

interface WorkItem {
  title: string;
  targetTool: string | null;
  reason: string;
  suggestedAction: string;
}

const CURRENT_SCHEMA = 1;
const MAX_TEXT_LEN = 200;
const MAX_ALT_LEN = 50;

// Patterns for defense-in-depth sanitization. The client is supposed to
// scrub these, but we re-apply them here so a buggy client can't leak
// raw paths into R2.
// Each path regex is non-greedy and stops at whitespace or quoted
// delimiters so surrounding text (e.g. "at C:\foo see details") survives.
const PATH_PATTERNS = [
  // Windows drive-letter path: C:\... or C:/... (stops at whitespace, quotes, < >)
  /[A-Za-z]:[\\\/](?:[^\\\/*?"<>|\s]*[\\\/])*[^\\\/*?"<>|\s]*/g,
  // UNC path: \\server\share\... (stops at whitespace, quotes, < >)
  /\\\\[^\s\\/:*?"<>|]+(?:\\[^\s\\/:*?"<>|]+)+/g,
  // Unix absolute under common roots
  /\/(?:home|Users|root|tmp|var|opt|etc|srv|mnt)\/[^\s'",<>]*/g,
];
const IPV4_PATTERN = /\b(?:(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\b/g;
const EMAIL_PATTERN = /\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b/g;
// Session IDs: 8+ char hex blobs. Shorter (4-7) are too likely to collide
// with timestamps or other benign numbers; leave them alone.
const SESSION_ID_PATTERN = /\b[a-f0-9]{8,40}\b/gi;

export function redactPaths(s: string): string {
  let out = s;
  for (const p of PATH_PATTERNS) {
    p.lastIndex = 0;
    out = out.replace(p, (m) => {
      // Keep the leading separator/driveletter to indicate a path was
      // redacted, but collapse the rest.
      if (m.startsWith("\\\\")) return "\\\\<path>";
      if (/^[A-Za-z]:/.test(m)) return `${m[0]}:\\<path>`;
      if (m.startsWith("/")) return "/<path>";
      return "<path>";
    });
  }
  return out;
}

export function redactIps(s: string): string {
  return s.replace(IPV4_PATTERN, "<ip>");
}

export function redactEmails(s: string): string {
  return s.replace(EMAIL_PATTERN, "<email>");
}

export function redactSessionIds(s: string): string {
  return s.replace(SESSION_ID_PATTERN, "<session-id>");
}

export function cap(s: string, max: number): string {
  if (s.length <= max) return s;
  return s.slice(0, max - 1) + "...";
}

export function sanitizeText(s: string, max: number): string {
  if (typeof s !== "string") return "";
  const scrubbed = redactSessionIds(redactEmails(redactIps(redactPaths(s.trim()))));
  return cap(scrubbed, max);
}

export function sanitizeToolFriction(t: ToolFriction): ToolFriction {
  return {
    tool: sanitizeText(t.tool ?? "", 100),
    invocations: typeof t.invocations === "number" ? Math.max(0, Math.floor(t.invocations)) : 0,
    blocked: typeof t.blocked === "number" ? Math.max(0, Math.floor(t.blocked)) : 0,
    abandoned: typeof t.abandoned === "number" ? Math.max(0, Math.floor(t.abandoned)) : 0,
    explicitFeedback: typeof t.explicitFeedback === "number" ? Math.max(0, Math.floor(t.explicitFeedback)) : 0,
    suggestedFix: sanitizeText(t.suggestedFix ?? "", MAX_TEXT_LEN),
  };
}

export function sanitizeBlocker(b: BlockerSummary): BlockerSummary {
  return {
    blocker: sanitizeText(b.blocker ?? "", 100),
    count: typeof b.count === "number" ? Math.max(0, Math.floor(b.count)) : 0,
    affectedTools: Array.isArray(b.affectedTools)
      ? b.affectedTools.slice(0, 20).map((t) => sanitizeText(t ?? "", 100))
      : [],
  };
}

export function sanitizeTransition(t: TransitionSummary): TransitionSummary {
  return {
    from: sanitizeText(t.from ?? "", 100),
    to: sanitizeText(t.to ?? "", 100),
    count: typeof t.count === "number" ? Math.max(0, Math.floor(t.count)) : 0,
  };
}

export function sanitizeFeedback(f: FeedbackSummary): FeedbackSummary {
  return {
    tool: sanitizeText(f.tool ?? "", 100),
    kind: sanitizeText(f.kind ?? "unknown", 50),
    count: typeof f.count === "number" ? Math.max(0, Math.floor(f.count)) : 0,
    reason: sanitizeText(f.reason ?? "", MAX_TEXT_LEN),
    alternative: f.alternative == null ? null : sanitizeText(f.alternative, MAX_ALT_LEN),
  };
}

export function sanitizeWorkItem(w: WorkItem): WorkItem {
  return {
    title: sanitizeText(w.title ?? "", MAX_TEXT_LEN),
    targetTool: w.targetTool == null ? null : sanitizeText(w.targetTool, 100),
    reason: sanitizeText(w.reason ?? "", MAX_TEXT_LEN),
    suggestedAction: sanitizeText(w.suggestedAction ?? "", MAX_TEXT_LEN),
  };
}

export function sanitizeReport(input: unknown): IncomingFrictionReport | { error: string } {
  if (typeof input !== "object" || input == null) {
    return { error: "body must be a JSON object" };
  }
  const r = input as Record<string, unknown>;

  if (typeof r.schemaVersion !== "number" || r.schemaVersion !== CURRENT_SCHEMA) {
    return { error: `unsupported schemaVersion: ${r.schemaVersion} (expected ${CURRENT_SCHEMA})` };
  }
  if (typeof r.sageFsVersion !== "string" || r.sageFsVersion.length > 50) {
    return { error: "sageFsVersion missing or too long" };
  }
  if (typeof r.submittedAtUtc !== "string") {
    return { error: "submittedAtUtc missing" };
  }

  return {
    schemaVersion: CURRENT_SCHEMA,
    sageFsVersion: sanitizeText(r.sageFsVersion, 50),
    submittedAtUtc: r.submittedAtUtc,
    totalEvents: typeof r.totalEvents === "number" ? Math.max(0, Math.floor(r.totalEvents)) : 0,
    totalFeedbackItems: typeof r.totalFeedbackItems === "number" ? Math.max(0, Math.floor(r.totalFeedbackItems)) : 0,
    toolsWithFriction: Array.isArray(r.toolsWithFriction)
      ? r.toolsWithFriction.slice(0, 20).map(sanitizeToolFriction)
      : [],
    topBlockers: Array.isArray(r.topBlockers)
      ? r.topBlockers.slice(0, 20).map(sanitizeBlocker)
      : [],
    frequentTransitions: Array.isArray(r.frequentTransitions)
      ? r.frequentTransitions.slice(0, 20).map(sanitizeTransition)
      : [],
    recentFeedback: Array.isArray(r.recentFeedback)
      ? r.recentFeedback.slice(0, 20).map(sanitizeFeedback)
      : [],
    recommendedWorkItems: Array.isArray(r.recommendedWorkItems)
      ? r.recommendedWorkItems.slice(0, 20).map(sanitizeWorkItem)
      : [],
  };
}

function makeKey(submittedAtUtc: string): string {
  // Use the submitted-at date for the prefix so the bucket self-organizes
  // by date. Append a random suffix to avoid collisions.
  const d = new Date(submittedAtUtc);
  const y = d.getUTCFullYear();
  const m = String(d.getUTCMonth() + 1).padStart(2, "0");
  const day = String(d.getUTCDate()).padStart(2, "0");
  const ts = d.getTime().toString(36);
  const rand = crypto.randomUUID().slice(0, 8);
  return `${y}/${m}/${day}/${ts}-${rand}.json`;
}

export function makeReportId(key: string): string {
  // Strip the .json and date prefix for a short human-readable id.
  const file = key.split("/").pop() ?? key;
  return file.replace(/\.json$/, "");
}

async function notifyDiscord(env: Env, reportId: string, r: IncomingFrictionReport): Promise<void> {
  if (!env.DISCORD_WEBHOOK_URL) return;
  const top = r.toolsWithFriction.slice(0, 3);
  const lines = top.map((t) => `• **${t.tool}** — ${t.explicitFeedback} feedback, ${t.blocked} blocked (${t.invocations} calls)`);
  const body = [
    `**Friction report \`${reportId}\`**`,
    `SageFs \`${r.sageFsVersion}\` · ${r.totalEvents} events · ${r.totalFeedbackItems} feedback items`,
    "",
    ...lines,
    "",
    top[0]?.suggestedFix ? `> ${top[0].suggestedFix}` : "",
  ].filter(Boolean).join("\n");

  try {
    await fetch(env.DISCORD_WEBHOOK_URL, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        username: "SageFs Friction",
        content: body.slice(0, 1900),
        // No embeds — keep the webhook payload simple and small.
      }),
    });
  } catch (err) {
    // Swallow: the submission is already in R2, Discord is best-effort.
    console.error("discord notify failed", err);
  }
}

/// Concatenate Uint8Array chunks into one buffer (used for capped body reads).
function concatBytes(chunks: Uint8Array[]): Uint8Array {
  const total = chunks.reduce((acc, c) => acc + c.byteLength, 0);
  const out = new Uint8Array(total);
  let offset = 0;
  for (const c of chunks) {
    out.set(c, offset);
    offset += c.byteLength;
  }
  return out;
}

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

/// Constant-time-ish compare for bearer tokens. (Node/Worker crypto.subtle
/// is async; this is a simple length-safe compare for a non-cryptographic
/// gate — the token is a high-entropy secret, and timing here is not a
/// practical leak surface for an R2 report store.)
function tokensEqual(a: string | null, b: string | undefined): boolean {
  if (!b || !a) return false;
  if (a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return diff === 0;
}

/// Owner-only gate: list stored report metadata (no contents). The ingest
/// path is open to every SageFs user, but reading reports is restricted to
/// the tool owner. `OWNER_TOKEN` must be configured or reads return 404
/// (indistinguishable from "nothing here").
async function handleOwnerList(req: Request, env: Env): Promise<Response> {
  if (!env.OWNER_TOKEN) {
    return jsonResponse(404, { error: "not found" });
  }
  const auth = req.headers.get("Authorization");
  const token = auth?.startsWith("Bearer ") ? auth.slice("Bearer ".length) : null;
  if (!tokensEqual(token, env.OWNER_TOKEN)) {
    return jsonResponse(401, { error: "invalid or missing owner token" });
  }
  try {
    const listed = await env.FRICTION_BUCKET.list();
    const reports = (listed.objects ?? []).map((o) => ({
      key: o.key,
      size: o.size,
      uploaded: o.uploaded,
      sageFsVersion: o.customMetadata?.sageFsVersion ?? null,
    }));
    return jsonResponse(200, { count: reports.length, reports });
  } catch (err) {
    return jsonResponse(500, { error: `R2 list failed: ${(err as Error).message}` });
  }
}

/// Owner-only gate: read a single stored report body by key.
async function handleOwnerGet(req: Request, env: Env, key: string): Promise<Response> {
  if (!env.OWNER_TOKEN) {
    return jsonResponse(404, { error: "not found" });
  }
  const auth = req.headers.get("Authorization");
  const token = auth?.startsWith("Bearer ") ? auth.slice("Bearer ".length) : null;
  if (!tokensEqual(token, env.OWNER_TOKEN)) {
    return jsonResponse(401, { error: "invalid or missing owner token" });
  }
  try {
    const obj = await env.FRICTION_BUCKET.get(key);
    if (!obj) {
      return jsonResponse(404, { error: `no report at key ${key}` });
    }
    const text = await obj.text();
    return new Response(text, {
      status: 200,
      headers: { "Content-Type": "application/json" },
    });
  } catch (err) {
    return jsonResponse(500, { error: `R2 get failed: ${(err as Error).message}` });
  }
}

/// Method router. POST / is the open ingest path (sanitized, size-capped).
/// GET /api/reports (owner) lists stored report metadata; GET /api/reports/{key}
/// (owner) returns one stored report. Everything read-side requires the owner
/// token — SageFs users can always submit, but only the tool owner can read.
export default {
  async fetch(req: Request, env: Env): Promise<Response> {
    const url = new URL(req.url);

    // Owner-only read endpoints.
    if (req.method === "GET" && url.pathname === "/api/reports") {
      return handleOwnerList(req, env);
    }
    if (req.method === "GET" && url.pathname.startsWith("/api/reports/")) {
      const key = decodeURIComponent(url.pathname.slice("/api/reports/".length));
      return handleOwnerGet(req, env, key);
    }

    if (req.method !== "POST") {
      return jsonResponse(405, { error: "POST only" });
    }
    if (url.pathname !== "/") {
      return jsonResponse(404, { error: "not found" });
    }

    // Token check (if configured).
    if (env.INGEST_TOKEN) {
      const got = req.headers.get("X-SageFs-Token");
      if (got !== env.INGEST_TOKEN) {
        return jsonResponse(401, { error: "invalid or missing X-SageFs-Token" });
      }
    }

    // Size cap. Content-Length is advisory only — chunked bodies (or lying
    // headers) bypass it. Read the body through a hard byte cap and reject
    // when the stream exceeds it, so the application-level limit always holds.
    const maxBytes = parseInt(env.MAX_PAYLOAD_BYTES ?? "65536", 10);
    const contentLength = parseInt(req.headers.get("Content-Length") ?? "0", 10);
    if (contentLength > maxBytes) {
      return jsonResponse(413, { error: `payload too large: ${contentLength} > ${maxBytes}` });
    }

    let rawText: string;
    try {
      if (!req.body) {
        return jsonResponse(400, { error: "empty body" });
      }
      const reader = req.body.getReader();
      const chunks: Uint8Array[] = [];
      let received = 0;
      for (;;) {
        const { done, value } = await reader.read();
        if (done) break;
        received += value.byteLength;
        if (received > maxBytes) {
          return jsonResponse(413, { error: `payload too large: > ${maxBytes} bytes` });
        }
        chunks.push(value);
      }
      rawText = new TextDecoder().decode(concatBytes(chunks));
    } catch (err) {
      return jsonResponse(400, { error: `failed to read body: ${(err as Error).message}` });
    }

    // Parse + sanitize.
    let raw: unknown;
    try {
      raw = JSON.parse(rawText);
    } catch (err) {
      return jsonResponse(400, { error: "invalid JSON body" });
    }
    const result = sanitizeReport(raw);
    if ("error" in result) {
      return jsonResponse(400, { error: result.error });
    }
    const report = result;

    // Write to R2.
    const key = makeKey(report.submittedAtUtc);
    const reportId = makeReportId(key);
    const stored = {
      ...report,
      receivedAt: new Date().toISOString(),
      reportId,
    };
    try {
      await env.FRICTION_BUCKET.put(key, JSON.stringify(stored, null, 2), {
        httpMetadata: { contentType: "application/json" },
        customMetadata: { sageFsVersion: report.sageFsVersion },
      });
    } catch (err) {
      return jsonResponse(500, { error: `R2 put failed: ${(err as Error).message}` });
    }

    // Best-effort Discord notification.
    await notifyDiscord(env, reportId, report);

    return jsonResponse(200, { reportId, key });
  },
};
