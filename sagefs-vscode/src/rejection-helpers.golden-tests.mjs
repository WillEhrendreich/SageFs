// WHY: the extension host is one process shared by every extension, and an `unhandledRejection` handler is
// process-wide. The old handler logged every rejection under a `[SageFs]` prefix, so Ionide's own failure to
// check a net480 project (issue #175) filled the SageFs output channel and read as a SageFs error. These pin
// that only a rejection whose stack runs through this extension's own folder is logged.
//
// Run: node src/rejection-helpers.golden-tests.mjs (also part of `npm run test:golden`).
import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import { createRequire } from "node:module";
import { fileURLToPath } from "node:url";
import path from "node:path";

const require = createRequire(import.meta.url);
const here = path.dirname(fileURLToPath(import.meta.url));
const { isOwnRejection, installRejectionHandler } = require(path.join(here, "rejection-helpers.js"));

const own = "/home/u/.vscode/extensions/willehrendreich.sagefs-0.6.929";
const stackIn = (root) => `Error: boom\n    at run (${root}/dist/extension.js:10:5)\n    at async main (${root}/dist/extension.js:20:1)`;

// Ionide's stack from the report, with the folder it runs from.
const ionide = "c:\\Users\\username\\.vscode-oss\\extensions\\ionide.ionide-fsharp-7.31.1\\bin\\net9.0";
const ionideStack = { stack: `Error: Check aborted (Aborted)\n    at check (${ionide}\\fsac.js:1:1)` };

assert.equal(isOwnRejection(own, { stack: stackIn(own) }), true, "a stack through our folder is ours");
assert.equal(isOwnRejection(own, ionideStack), false, "another extension's stack is not ours");

// Windows paths come back with backslashes and a different drive-letter case.
assert.equal(
  isOwnRejection("C:\\Users\\u\\.vscode\\extensions\\willehrendreich.sagefs-0.6.929",
    { stack: "Error: x\n    at f (c:/users/u/.vscode/extensions/willehrendreich.sagefs-0.6.929/dist/extension.js:1:1)" }),
  true, "slash direction and case do not change whose stack it is");

// A folder that only shares our prefix belongs to somebody else.
assert.equal(isOwnRejection(own, { stack: stackIn(own + "-other") }), false, "a sibling folder with our prefix is not ours");

// Nothing to attribute: no stack, a bare string, null.
assert.equal(isOwnRejection(own, "just a string"), false, "a string has no stack to attribute");
assert.equal(isOwnRejection(own, null), false, "null has no stack to attribute");
assert.equal(isOwnRejection("", { stack: stackIn(own) }), false, "an empty root attributes nothing");

// The handler: logs ours with the prefix, drops the rest.
const target = new EventEmitter();
const logged = [];
installRejectionHandler(own, (line) => logged.push(line), target);
target.emit("unhandledRejection", ionideStack);
assert.equal(logged.length, 0, "Ionide's rejection is not logged");
target.emit("unhandledRejection", { stack: stackIn(own) });
assert.equal(logged.length, 1, "our rejection is logged once");
assert.ok(logged[0].startsWith("[SageFs] Unhandled rejection: "), "and carries the prefix");
assert.ok(logged[0].includes("Error: boom"), "and the stack");

console.log("rejection-helpers golden tests: ok");
