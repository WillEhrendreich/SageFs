// WHY: `SageFs: Switch Workflow` always failed with "unknown workflow ''" because httpPost wrote its
// body without a Content-Length, so Node sent it `Transfer-Encoding: chunked`, and the daemon's
// workflow route only read a body that declared a length. These pin the request the daemon sees:
// a POST always carries Content-Length, counted in bytes (not characters), and is never chunked.
//
// Run: node src/http-helpers.golden-tests.mjs (also part of `npm run test:golden`).
import http from "node:http";
import assert from "node:assert/strict";
import { createRequire } from "node:module";
import { fileURLToPath } from "node:url";
import path from "node:path";

const require = createRequire(import.meta.url);
const here = path.dirname(fileURLToPath(import.meta.url));
const { httpPost } = require(path.join(here, "http-helpers.js"));

const received = [];
const server = http.createServer((req, res) => {
  const chunks = [];
  req.on("data", (c) => chunks.push(c));
  req.on("end", () => {
    received.push({
      headers: req.headers,
      bytes: Buffer.concat(chunks).length,
      body: Buffer.concat(chunks).toString("utf8"),
    });
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end("{}");
  });
});

await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
const url = `http://127.0.0.1:${server.address().port}/api/sessions/abc/workflow`;

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

await check("a POST body declares its Content-Length", async () => {
  received.length = 0;
  await httpPost(url, JSON.stringify({ workflow: "HotReload" }), 5000);
  assert.equal(received.length, 1);
  assert.equal(received[0].headers["content-length"], String(received[0].bytes));
});

await check("a POST body is never sent chunked", async () => {
  received.length = 0;
  await httpPost(url, JSON.stringify({ workflow: "HotReload" }), 5000);
  assert.equal(received[0].headers["transfer-encoding"], undefined);
});

await check("Content-Length counts bytes, so a multi-byte character does not truncate the body", async () => {
  received.length = 0;
  const body = JSON.stringify({ code: "let s = \"naïve ✓ 日本\"" });
  await httpPost(url, body, 5000);
  assert.equal(received[0].headers["content-length"], String(Buffer.byteLength(body)));
  assert.equal(received[0].body, body);
});

await check("an empty object body still declares its length", async () => {
  received.length = 0;
  await httpPost(url, "{}", 5000);
  assert.equal(received[0].headers["content-length"], "2");
});

server.close();
if (failures > 0) {
  console.log(`${failures} failed`);
  process.exit(1);
}
console.log("all passed");
