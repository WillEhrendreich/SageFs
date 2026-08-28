/**
 * Local unit tests for sanitization. Run with:
 *   npx tsx src/sanitize.test.ts
 *
 * These don't need the Workers runtime — they test pure functions.
 */

import {
  sanitizeReport,
  sanitizeText,
  redactPaths,
  redactIps,
  redactEmails,
  redactSessionIds,
  cap,
} from "./index";

let passed = 0;
let failed = 0;
const failures: string[] = [];

function eq<T>(actual: T, expected: T, name: string): void {
  const a = JSON.stringify(actual);
  const e = JSON.stringify(expected);
  if (a === e) {
    passed++;
    console.log(`  ✓ ${name}`);
  } else {
    failed++;
    failures.push(`${name}\n    actual:   ${a}\n    expected: ${e}`);
    console.log(`  ✗ ${name}\n    actual:   ${a}\n    expected: ${e}`);
  }
}

function truthy(actual: unknown, name: string): void {
  if (actual) {
    passed++;
    console.log(`  ✓ ${name}`);
  } else {
    failed++;
    failures.push(`${name} (expected truthy, got ${JSON.stringify(actual)})`);
    console.log(`  ✗ ${name}`);
  }
}

console.log("sanitizeText — paths");
eq(redactPaths("see C:\\Users\\foo\\bar\\baz for details"), "see C:\\<path> for details", "Windows drive-letter path collapsed");
eq(redactPaths("see \\\\server\\share\\file for details"), "see \\\\<path> for details", "UNC path collapsed");
eq(redactPaths("from /home/user/project to /tmp/x"), "from /<path> to /<path>", "Unix absolute paths collapsed");
eq(redactPaths("just a string with no paths"), "just a string with no paths", "no-op when no paths");

console.log("sanitizeText — IPs and emails");
eq(redactIps("server 10.0.0.1 was down"), "server <ip> was down", "internal IP redacted");
eq(redactIps("public 8.8.8.8 too"), "public <ip> too", "public IP redacted");
eq(redactEmails("contact user@example.com please"), "contact <email> please", "email redacted");

console.log("sanitizeText — session IDs");
eq(redactSessionIds("session 4d55f947 was active"), "session <session-id> was active", "8-char hex redacted");
eq(redactSessionIds("no hex here"), "no hex here", "no-op when no hex");

console.log("sanitizeText — combined + cap");
eq(cap("hello", 10), "hello", "no cap when under limit");
eq(cap("hello world", 5), "hell...", "cap with three dots");
const redacted = sanitizeText("failed on C:\\Users\\alice\\my_secret\\project at 192.168.1.1, email alice@corp.com", 200);
truthy(!redacted.includes("alice"), `combined: should not contain "alice", got "${redacted}"`);
truthy(!redacted.includes("192.168"), `combined: should not contain "192.168", got "${redacted}"`);
truthy(!redacted.includes("@corp.com"), `combined: should not contain email, got "${redacted}"`);
truthy(redacted.includes("<path>"), "combined: should contain <path>");
truthy(redacted.includes("<ip>"), "combined: should contain <ip>");

console.log("sanitizeReport — schema validation");
const badSchema = sanitizeReport({ schemaVersion: 99, sageFsVersion: "0.6.0", submittedAtUtc: "2026-01-01T00:00:00Z" });
truthy("error" in badSchema, "wrong schema rejected");
const notObject = sanitizeReport("not an object");
truthy("error" in notObject, "non-object rejected");
const missingFields = sanitizeReport({ schemaVersion: 1 });
truthy("error" in missingFields, "missing fields rejected");

console.log("sanitizeReport — full round trip");
const input = {
  schemaVersion: 1,
  sageFsVersion: "0.6.315",
  submittedAtUtc: "2026-01-01T00:00:00.000Z",
  totalEvents: 16,
  totalFeedbackItems: 4,
  toolsWithFriction: [
    {
      tool: "send_fsharp_code",
      invocations: 100,
      blocked: 0,
      abandoned: 0,
      explicitFeedback: 4,
      suggestedFix: "Path C:\\Users\\bob\\project at 10.0.0.1 should be ignored",
    },
  ],
  topBlockers: [
    { blocker: "SessionMissing", count: 3, affectedTools: ["send_fsharp_code", "list_sessions"] },
  ],
  frequentTransitions: [
    { from: "list_sessions", to: "switch_session", count: 12 },
  ],
  recentFeedback: [
    {
      tool: "send_fsharp_code",
      kind: "ResultDidNotEstablishTrust",
      count: 4,
      reason: "Dashboard went into 'Server disconnected' at C:\\Users\\bob\\secret for project bob@corp.com",
      alternative: "n/a",
    },
  ],
  recommendedWorkItems: [
    {
      title: "Fix send_fsharp_code connection state",
      targetTool: "send_fsharp_code",
      reason: "Blocked=0, abandoned=0, explicitFeedback=4",
      suggestedAction: "Email bob@corp.com about the issue",
    },
  ],
};

const r = sanitizeReport(input);
if ("error" in r) {
  failed++;
  console.log(`  ✗ full round trip rejected: ${r.error}`);
} else {
  eq(r.schemaVersion, 1, "schemaVersion preserved");
  eq(r.sageFsVersion, "0.6.315", "sageFsVersion preserved");
  eq(r.toolsWithFriction.length, 1, "tools array preserved");
  const t = r.toolsWithFriction[0]!;
  eq(t.tool, "send_fsharp_code", "tool name preserved");
  truthy(!t.suggestedFix.includes("Users\\alice"), `alice path redacted, got: ${t.suggestedFix}`);
  truthy(!t.suggestedFix.includes("10.0.0.1"), `IP redacted in suggestedFix, got: ${t.suggestedFix}`);
  const fb = r.recentFeedback[0]!;
  truthy(!fb.reason.includes("C:\\Users\\bob"), `path redacted in reason, got: ${fb.reason}`);
  truthy(!fb.reason.includes("bob@corp.com"), `email redacted in reason, got: ${fb.reason}`);
  truthy(fb.reason.includes("<path>"), "reason contains <path>");
  const wi = r.recommendedWorkItems[0]!;
  truthy(!wi.suggestedAction.includes("bob@corp.com"), `email redacted in suggestedAction, got: ${wi.suggestedAction}`);
  truthy(wi.suggestedAction.includes("<email>"), "suggestedAction contains <email>");
  truthy(fb.reason.length <= 200, `reason capped to 200 chars, got ${fb.reason.length}`);
}

console.log(`\n${passed} passed, ${failed} failed`);
if (failed > 0) {
  console.log("\nFailures:");
  for (const f of failures) console.log(`  - ${f}`);
  process.exit(1);
}
