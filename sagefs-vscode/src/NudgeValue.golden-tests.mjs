// WHY: the roadmap's VS Code knob may only "write through the nudge door", and the door is the
// daemon's own `nudge_value` tool (SageFs/McpNudge.fs) — journals, the `seen` hash check,
// ownership and the type-check rollback live THERE, so this extension must reach the tool
// itself, not a hand-rolled write that looks like one. This drives the real compiled client
// (fable-out/SageFsClient.js) against a local endpoint that speaks the daemon's MCP
// streamable-HTTP handshake exactly as scripts/reconnect-mcp.fsx proves it against a live one,
// and records what the wire actually carried: `initialize` first, the minted `Mcp-Session-Id`
// taken from the response HEADERS, then `tools/call` with name `nudge_value` and the arguments
// the tool documents. A 404 (the daemon was replaced under this client) must heal by
// re-initializing, not by failing forever.
//
// Run: node --no-warnings src/NudgeValue.golden-tests.mjs (also part of `npm run test:golden`).
import http from "node:http";
import assert from "node:assert/strict";
import { fileURLToPath } from "node:url";
import path from "node:path";

const here = path.dirname(fileURLToPath(import.meta.url));
const Client = await import(path.join(here, "../fable-out/SageFsClient.js"));

const HASH_A = "a".repeat(64);
const HASH_B = "b".repeat(64);

// The tool's own reply shapes, as SageFs/McpNudge.render writes them.
const writtenReply = JSON.stringify({
  outcome: "Written",
  file: "/w/Tuning.fs",
  address: "Game.Tuning.tuning/{JumpVelocity}",
  before: "9.87",
  after: "9.88",
  hashAfter: HASH_B,
  fileHashBefore: "c".repeat(64),
  fileHashAfter: "d".repeat(64),
  eventId: 12,
  notes: ["FileNotWatched"],
});
const refusedReply = JSON.stringify({
  outcome: "Refused",
  refusal: "SourceMoved",
  rule: "The expression changed since you inspected it. It is now 9.9 (hash bbb…, and you saw hash aaa…).",
  nextAction: "Inspect the address again and send the write from what is there now.",
});
const inspectedReply = JSON.stringify({
  outcome: "Inspected",
  file: "/w/Tuning.fs",
  fileHash: "e".repeat(64),
  journaled: 3,
  undoSteps: 1,
  redoSteps: 0,
  listing: "Complete",
  items: [
    { address: "Game.Tuning.tuning/{JumpVelocity}", text: "9.87", hash: HASH_A, line: 5, column: 10, endLine: 5, endColumn: 14, kind: "Knob", valueKind: "Real", value: 9.87 },
    { address: "Game.Tuning.tuning/{MaxSteps}", text: "0x1F", hash: "1".repeat(64), line: 6, column: 12, endLine: 6, endColumn: 16, kind: "Knob", valueKind: "Integer", value: 31 },
    { address: "Game.Tuning.tuning/{Wind}", text: "\"west\"", hash: "2".repeat(64), line: 7, column: 12, endLine: 7, endColumn: 18, kind: "Knob", valueKind: "Text", value: "west" },
    { address: "Game.Tuning.tuning/{Gravity}", text: "gravity * 2.0", hash: "3".repeat(64), line: 8, column: 12, endLine: 8, endColumn: 25, kind: "Formula" },
  ],
  notes: [],
});
const unchangedReply = JSON.stringify({
  outcome: "Unchanged",
  address: "Game.Tuning.tuning/{JumpVelocity}",
  text: "9.88",
  notes: [],
});

const inits = [];
const calls = [];
let minted = "sess-0";
let initCount = 0;
let replyQueue = [];
let sseNext = false;

const jsonRpc = (id, text) =>
  JSON.stringify({ jsonrpc: "2.0", id, result: { content: [{ type: "text", text }], isError: false } });
const sseFrame = (id, text) =>
  `event: message\ndata: ${JSON.stringify({ jsonrpc: "2.0", id, result: { content: [{ type: "text", text }] } })}\n\n`;

const server = http.createServer((req, res) => {
  const chunks = [];
  req.on("data", (c) => chunks.push(c));
  req.on("end", () => {
    const raw = Buffer.concat(chunks).toString("utf8");
    const msg = JSON.parse(raw);
    if (msg.method === "initialize") {
      initCount += 1;
      minted = `sess-${initCount}`;
      inits.push({ headers: req.headers, minted, body: msg });
      res.writeHead(200, { "Content-Type": "application/json", "Mcp-Session-Id": minted });
      res.end(JSON.stringify({ jsonrpc: "2.0", id: msg.id, result: { protocolVersion: "2024-11-05", capabilities: {}, serverInfo: { name: "fake-daemon", version: "1" } } }));
      return;
    }
    if (msg.method === "tools/call") {
      calls.push({ headers: req.headers, body: msg });
      if (req.headers["mcp-session-id"] !== minted) {
        // What the real daemon answers a session id it no longer holds.
        res.writeHead(404, { "Content-Type": "application/json" });
        res.end(JSON.stringify({ error: { code: -32000, message: "Bad Request: session expired" } }));
        return;
      }
      const text = replyQueue.shift();
      if (text === undefined) {
        res.writeHead(500, { "Content-Type": "application/json" });
        res.end(JSON.stringify({ error: { code: -32603, message: "the test queued no reply" } }));
        return;
      }
      res.writeHead(200, { "Content-Type": sseNext ? "text/event-stream" : "application/json" });
      res.end(sseNext ? sseFrame(msg.id, text) : jsonRpc(msg.id, text));
      return;
    }
    res.writeHead(404);
    res.end();
  });
});

await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
const port = server.address().port;
const client = Client.create(port, port + 1, () => {});

let failures = 0;
async function check(name, run) {
  try {
    await run();
    console.log(`ok   ${name}`);
  } catch (err) {
    failures++;
    console.log(`FAIL ${name}\n     ${err.message}`);
  }
}

// Fable's runtime shapes: Result is FSharpResult$2 with tag 0 = Ok, 1 = Error; a DU case is
// tag + fields; an F# list is cons cells with head/tail, the empty list having no head.
const okOf = (result) => {
  assert.equal(result.tag, 0, `expected Ok, got Error: ${result.fields?.[0]}`);
  return result.fields[0];
};
const listToArray = (list) => {
  const out = [];
  let cur = list;
  while (cur != null && cur.head != null) {
    out.push(cur.head);
    cur = cur.tail;
  }
  return out;
};

const setCall = (seen) => ({
  Action: "set",
  File: "/w/Tuning.fs",
  Address: "Game.Tuning.tuning/{JumpVelocity}",
  Seen: seen,
  Literal: "9.88",
  Expression: "",
  SessionId: "abc12345",
  WorkingDirectory: "/w",
});

await check("initialize happens first, takes the session id from the HEADERS, and carries the MCP Accept", async () => {
  const before = initCount;
  replyQueue.push(writtenReply);
  const result = okOf(await Client.nudgeValue(setCall(HASH_A), client));
  assert.equal(initCount, before + 1, "one initialize was sent");
  const init = inits[inits.length - 1];
  assert.match(init.headers["accept"] || "", /application\/json/);
  assert.match(init.headers["accept"] || "", /text\/event-stream/);
  assert.equal(init.headers["mcp-session-id"], undefined, "initialize presents no session id");
  assert.equal(result.tag, 0, "the set was Written");
});

await check("the write is tools/call nudge_value, on the minted session, with the tool's own arguments", async () => {
  const call = calls[calls.length - 1];
  assert.equal(call.body.params.name, "nudge_value");
  assert.equal(call.headers["mcp-session-id"], inits[inits.length - 1].minted);
  assert.match(call.headers["accept"] || "", /text\/event-stream/);
  assert.deepEqual(call.body.params.arguments, {
    action: "set",
    file: "/w/Tuning.fs",
    address: "Game.Tuning.tuning/{JumpVelocity}",
    seen: HASH_A,
    literal: "9.88",
    expression: "",
    working_directory: "/w",
    session_id: "abc12345",
  });
});

await check("a Written reply comes back as the door's facts: before, after, hashAfter, notes", async () => {
  replyQueue.push(writtenReply);
  const result = okOf(await Client.nudgeValue(setCall(HASH_A), client));
  assert.equal(result.tag, 0, "NudgeWritten");
  const [file, address, before, after, hashAfter, notes] = result.fields;
  assert.equal(file, "/w/Tuning.fs");
  assert.equal(address, "Game.Tuning.tuning/{JumpVelocity}");
  assert.equal(before, "9.87");
  assert.equal(after, "9.88");
  assert.equal(hashAfter, HASH_B);
  assert.deepEqual(listToArray(notes), ["FileNotWatched"]);
});

await check("a refusal is DATA, not a transport error: rule and next action reach the caller", async () => {
  replyQueue.push(refusedReply);
  const result = okOf(await Client.nudgeValue(setCall(HASH_B), client));
  assert.equal(result.tag, 3, "NudgeRefused");
  const [token, rule, nextAction] = result.fields;
  assert.equal(token, "SourceMoved");
  assert.match(rule, /changed since you inspected/);
  assert.match(nextAction, /Inspect the address again/);
});

await check("inspect lists the file's knobs with their spans, and a Formula or Text has no drag", async () => {
  replyQueue.push(inspectedReply);
  const result = okOf(
    await Client.nudgeValue(
      { ...setCall(""), Action: "inspect", Address: "", Seen: "", Literal: "", SessionId: null },
      client
    )
  );
  assert.equal(result.tag, 1, "NudgeInspected");
  const [file, knobs, undoSteps, redoSteps] = result.fields;
  assert.equal(file, "/w/Tuning.fs");
  assert.equal(undoSteps, 1);
  assert.equal(redoSteps, 0);
  const items = listToArray(knobs);
  assert.equal(items.length, 4);
  const [real, int, text, formula] = items;
  assert.equal(real.File, "/w/Tuning.fs");
  assert.equal(real.Line, 5);
  assert.equal(real.StartCol, 10);
  assert.equal(real.EndCol, 14);
  assert.equal(real.Scrub.tag, 1, "a Real is a Fraction");
  assert.equal(real.Scrub.fields[1], 2, "9.87 spells 2 decimals");
  assert.equal(int.Scrub.tag, 0, "an Integer is Whole");
  assert.equal(int.Scrub.fields[0], 31n, "0x1F is 31");
  assert.equal(text.Scrub.tag, 2, "a Text has no drag");
  assert.equal(formula.Scrub.tag, 2, "a Formula has no drag");
  assert.equal(formula.Text, "gravity * 2.0");
});

await check("the transport may answer with an SSE frame; the message is read out of it", async () => {
  replyQueue.push(unchangedReply);
  sseNext = true;
  const result = okOf(await Client.nudgeValue(setCall(HASH_A), client));
  sseNext = false;
  assert.equal(result.tag, 2, "NudgeUnchanged");
  assert.equal(result.fields[1], "9.88", "the text that is already there");
});

await check("a daemon replaced under this client heals: 404 -> fresh initialize -> the call lands", async () => {
  // The daemon holds MCP sessions in memory; a restart makes the minted id unknown. Rotate the
  // server's id WITHOUT an initialize, exactly what a restart looks like to this client.
  minted = "sess-after-restart";
  const initsBefore = initCount;
  replyQueue.push(writtenReply);
  const result = okOf(await Client.nudgeValue(setCall(HASH_A), client));
  assert.equal(result.tag, 0, "the retried set was Written");
  assert.equal(initCount, initsBefore + 1, "exactly one re-initialize");
  const call = calls[calls.length - 1];
  assert.equal(
    call.headers["mcp-session-id"],
    inits[inits.length - 1].minted,
    "the retry rides the id the fresh initialize minted"
  );
  assert.notEqual(call.headers["mcp-session-id"], "sess-after-restart", "the dead id is never presented again");
});

server.close();
if (failures > 0) {
  console.log(`${failures} failed`);
  process.exit(1);
}
console.log("all passed");
