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

// WHY (#173): with no daemon running, the sessions tree polls on a timer and every failed poll wrote
// "[warn] listSessions: AggregateError" to the output channel, so a person who had just installed the
// extension saw a wall of identical lines. A daemon that is down is reported once; the next report is
// for the next outage, after the daemon has answered again.
const deadServer = http.createServer();
await new Promise((resolve) => deadServer.listen(0, resolve));
const deadPort = deadServer.address().port;
await new Promise((resolve) => deadServer.close(resolve));

await check("a daemon that is down is reported once, not on every poll (#173)", async () => {
  const lines = [];
  const down = Client.create(deadPort, deadPort + 1, (line) => lines.push(line));
  for (let poll = 0; poll < 5; poll++) await Client.listSessions(down);
  const warnings = lines.filter((line) => line.includes("listSessions"));
  assert.equal(warnings.length, 1, `five polls, ${warnings.length} warnings: ${warnings.join(" | ")}`);
});

await check("a second outage after the daemon answered is reported again", async () => {
  const lines = [];
  const flaky = Client.create(deadPort, deadPort + 1, (line) => lines.push(line));
  await Client.getRaw("probe", "/x", 1000, flaky);
  await Client.getRaw("probe", "/x", 1000, flaky);
  flaky.mcpPort = port;
  await Client.getRaw("probe", "/x", 1000, flaky);
  flaky.mcpPort = deadPort;
  await Client.getRaw("probe", "/x", 1000, flaky);
  const warnings = lines.filter((line) => line.includes("[warn] probe"));
  assert.equal(warnings.length, 2, `two outages, ${warnings.length} warnings: ${lines.join(" | ")}`);
});

// WHY (#173, second report): the guard above covered the GET helpers only. getCompletions, explore and
// exploreCompletions log their own "[warn]" line, so with no daemon running every keystroke in a .fsx file
// wrote "[warn] getCompletions: AggregateError". Every call that can fail while the daemon is down goes
// through the same once-per-outage report.
const postCalls = [
  ["getCompletions", (c) => Client.getCompletions("List.", 5, undefined, c)],
  ["explore", (c) => Client.explore("List", c)],
  ["exploreCompletions", (c) => Client.exploreCompletions("List", "sid", c)],
];

for (const [name, call] of postCalls) {
  await check(`${name} against a daemon that is down is reported once, not on every call (#173)`, async () => {
    const lines = [];
    const down = Client.create(deadPort, deadPort + 1, (line) => lines.push(line));
    for (let keystroke = 0; keystroke < 5; keystroke++) await call(down);
    const warnings = lines.filter((line) => line.includes(`[warn] ${name}`));
    assert.equal(warnings.length, 1, `five calls, ${warnings.length} warnings: ${lines.join(" | ")}`);
  });

  await check(`${name} reports a second outage after the daemon answered`, async () => {
    const lines = [];
    // The daemon is up, goes down for two calls, comes back, and goes down again. The record of which calls
    // are failing is per process, so the sequence starts with an answer and does not inherit the last test's outage.
    const flaky = Client.create(port, deadPort + 1, (line) => lines.push(line));
    await call(flaky);
    flaky.mcpPort = deadPort;
    await call(flaky);
    await call(flaky);
    flaky.mcpPort = port;
    await call(flaky);
    flaky.mcpPort = deadPort;
    await call(flaky);
    const warnings = lines.filter((line) => line.includes(`[warn] ${name}`));
    assert.equal(warnings.length, 2, `two outages, ${warnings.length} warnings: ${lines.join(" | ")}`);
  });
}

server.close();
if (failures > 0) {
  console.log(`${failures} failed`);
  process.exit(1);
}
console.log("all passed");
