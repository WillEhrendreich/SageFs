// WHY: with a second session on the daemon, "SageFs: Enable Live Testing" acted on whichever
// session the DAEMON had active, not the one the window was showing, so live testing found no
// tests until the user picked the session by id. Every session-scoped call the extension makes now
// names its session. Where the daemon route can take a session id, the id is in the body. Where it
// cannot (reset, hard reset, cancel, run policy), the extension first makes that session the
// daemon's active one, so the call does not land on someone else's.
//
// This drives the real compiled client (fable-out/SageFsClient.js) against a local HTTP server and
// records what it sends. Run `npm run compile` first.
//
// Run: node --no-warnings src/SageFsClient.golden-tests.mjs (also part of `npm run test:golden`).
import http from "node:http";
import assert from "node:assert/strict";
import { fileURLToPath } from "node:url";
import path from "node:path";

const here = path.dirname(fileURLToPath(import.meta.url));
const Client = await import(path.join(here, "../fable-out/SageFsClient.js"));

const seen = [];
const server = http.createServer((req, res) => {
  const chunks = [];
  req.on("data", (c) => chunks.push(c));
  req.on("end", () => {
    const text = Buffer.concat(chunks).toString("utf8");
    seen.push({ method: req.method, url: req.url, body: text.length > 0 ? JSON.parse(text) : null });
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(JSON.stringify({ success: true, message: "ok" }));
  });
});
await new Promise((resolve) => server.listen(0, resolve));
const port = server.address().port;
const client = Client.create(port, port + 1, () => {});

let failures = 0;
async function check(name, run) {
  seen.length = 0;
  try {
    await run();
    console.log(`ok   ${name}`);
  } catch (err) {
    failures++;
    console.log(`FAIL ${name}\n     ${err.message}`);
  }
}

const SID = "abc12345";
const requests = () => seen.map((r) => `${r.method} ${r.url}`);

await check("Enable Live Testing names its session in the body", async () => {
  await Client.enableLiveTesting(SID, client);
  assert.deepEqual(requests(), ["POST /api/live-testing/enable"]);
  assert.equal(seen[0].body.sessionId, SID);
});

await check("Disable Live Testing names its session in the body", async () => {
  await Client.disableLiveTesting(SID, client);
  assert.deepEqual(requests(), ["POST /api/live-testing/disable"]);
  assert.equal(seen[0].body.sessionId, SID);
});

await check("Run Tests names its session and keeps its pattern", async () => {
  await Client.runTests(SID, "Suite.adds", client);
  assert.deepEqual(requests(), ["POST /api/live-testing/run"]);
  assert.equal(seen[0].body.sessionId, SID);
  assert.equal(seen[0].body.pattern, "Suite.adds");
});

await check("Load Script evaluates in its session", async () => {
  await Client.loadScript(SID, "/w/a.fsx", client);
  assert.deepEqual(requests(), ["POST /exec"]);
  assert.equal(seen[0].body.sessionId, SID);
  assert.match(seen[0].body.code, /a\.fsx/);
});

await check("Reset makes its session the active one first, because the route cannot be told", async () => {
  await Client.resetSession(SID, client);
  assert.deepEqual(requests(), ["POST /api/sessions/switch", "POST /reset"]);
  assert.equal(seen[0].body.sessionId, SID);
});

await check("Hard Reset makes its session the active one first", async () => {
  await Client.hardReset(SID, true, client);
  assert.deepEqual(requests(), ["POST /api/sessions/switch", "POST /hard-reset"]);
  assert.equal(seen[0].body.sessionId, SID);
  assert.equal(seen[1].body.rebuild, true);
});

await check("Cancel makes its session the active one first", async () => {
  await Client.cancelEval(SID, client);
  assert.deepEqual(requests(), ["POST /api/sessions/switch", "POST /api/cancel-eval"]);
  assert.equal(seen[0].body.sessionId, SID);
});

await check("Set Run Policy makes its session the active one first", async () => {
  await Client.setRunPolicy(SID, "Unit", "Always", client);
  assert.deepEqual(requests(), ["POST /api/sessions/switch", "POST /api/live-testing/policy"]);
  assert.equal(seen[0].body.sessionId, SID);
  assert.equal(seen[1].body.category, "Unit");
});

server.close();
if (failures > 0) {
  console.log(`${failures} failed`);
  process.exit(1);
}
console.log("all passed");
