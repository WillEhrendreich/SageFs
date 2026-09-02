import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import vm from "node:vm";

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const fixturesDir = path.resolve(__dirname, "../../SageFs.Tests/Fixtures/LiveTesting");

const source = fs.readFileSync(path.resolve(__dirname, "../fable-out/LiveTestingListener.js"), "utf8");

function extractFunction(functionName) {
  const marker = `export function ${functionName}`;
  const start = source.indexOf(marker);
  if (start < 0) throw new Error(`Could not find ${functionName}`);
  const braceStart = source.indexOf("{", start);
  let depth = 0;
  let end = braceStart;
  for (; end < source.length; end++) {
    const ch = source[end];
    if (ch === "{") depth++;
    else if (ch === "}") {
      depth--;
      if (depth === 0) {
        end++;
        break;
      }
    }
  }
  return source.slice(start, end).replace("export function", "function");
}

const harness = `
${extractFunction("parseSelectionPrecision")}
${extractFunction("parseFreshnessTrust")}
${extractFunction("parseRerunCause")}
${extractFunction("parseStringArrayField")}
${extractFunction("parseLastDecision")}
${extractFunction("parseSummary")}
${extractFunction("parseFreshness")}
${extractFunction("parseCompletion")}
${extractFunction("parseResultsBatch")}
module.exports = { parseSummary, parseResultsBatch };
`;

const context = {
  module: { exports: {} },
  exports: {},
  defaultArg: (x, y) => (x === undefined ? y : x),
  bind: (f, x) => (x === undefined ? undefined : f(x)),
  map: (f, x) => (x === undefined ? undefined : f(x)),
  map_1: (f, xs) => xs.map(f),
  some: (x) => x,
  fieldInt: (name, obj) => Number.isInteger(obj?.[name]) ? obj[name] : undefined,
  fieldString: (name, obj) => typeof obj?.[name] === "string" ? obj[name] : undefined,
  fieldObj: (name) => (obj) => obj?.[name],
  tryCastArray: (x) => Array.isArray(x) ? x : undefined,
  tryCastString: (x) => typeof x === "string" ? x : undefined,
  fieldArray: (name, obj) => Array.isArray(obj?.[name]) ? obj[name] : undefined,
  parseDuCase: (du) => du?.Case,
  duFieldsArr: (du) => du?.Fields,
  parseTestInfo: (entry) => ({ entry }),
  parseTestResult: (entry) => ({ entry }),
  parseFreshness: (data) => ({ tag: 0 }),
  ofArray: (xs) => xs,
  empty: () => [],
  choose: (f, arr) => arr.map(f).filter((x) => x !== undefined),
  toInt64: (x) => x,
  fromInt32: (x) => x,
  VscSelectionPrecision: function(tag) { this.tag = tag; },
  VscFreshnessTrust: function(tag) { this.tag = tag; },
  VscRerunCause: function(tag) { this.tag = tag; },
  VscResultFreshness: function(tag) { this.tag = tag; },
  VscLiveTestingDecision: function(Cause, FilePath, Precision, Trust, ChangedSymbols, SelectedTests, DeferredTests, Reason) {
    this.Cause = Cause;
    this.FilePath = FilePath;
    this.Precision = Precision;
    this.Trust = Trust;
    this.ChangedSymbols = ChangedSymbols;
    this.SelectedTests = SelectedTests;
    this.DeferredTests = DeferredTests;
    this.Reason = Reason;
  },
  VscTestSummary: function(Total, Passed, Failed, Running, Stale, Disabled, DiscoveryState, DiscoveryGeneration, LastDecision) {
    this.Total = Total;
    this.Passed = Passed;
    this.Failed = Failed;
    this.Running = Running;
    this.Stale = Stale;
    this.Disabled = Disabled;
    this.DiscoveryState = DiscoveryState;
    this.DiscoveryGeneration = DiscoveryGeneration;
    this.LastDecision = LastDecision;
  },
  VscLiveTestEvent: function(tag, fields) {
    this.tag = tag;
    this.fields = fields;
  },
};

vm.runInNewContext(harness, context);

const { parseSummary, parseResultsBatch } = context.module.exports;

function readFixture(name) {
  return JSON.parse(fs.readFileSync(path.join(fixturesDir, name), "utf8"));
}

function assert(condition, message) {
  if (!condition) {
    throw new Error(message);
  }
}

function run(name, fn) {
  try {
    fn();
    console.log(`PASS ${name}`);
  } catch (error) {
    console.error(`FAIL ${name}`);
    console.error(error instanceof Error ? error.message : String(error));
    process.exitCode = 1;
  }
}

run("parseSummary consumes fallback decision golden fixture", () => {
  const summary = parseSummary(readFixture("summary-with-fallback-decision.json"));
  assert(summary.Total === 5, `expected Total=5, got ${summary.Total}`);
  assert(summary.LastDecision != null, "expected LastDecision to be present");
  assert(summary.LastDecision.Precision.tag === 2, "expected ConservativeFallback precision");
  assert(summary.LastDecision.Reason === "fallback rebuild", `unexpected reason: ${summary.LastDecision.Reason}`);
});

run("parseResultsBatch consumes coverage decision golden fixture", () => {
  const events = Array.from(parseResultsBatch(2n, readFixture("results-batch-with-coverage-decision.json")));
  assert(events.length === 2, `expected 2 events, got ${events.length}`);
  const discovered = events[0];
  const batch = events[1];
  assert(discovered.tag === 0, `expected TestsDiscovered tag 0, got ${discovered.tag}`);
  assert(discovered.fields.length === 3, `expected (tests, isComplete, generation), got ${discovered.fields.length} fields`);
  assert(discovered.fields[0].length === 2, `expected 2 discovered tests, got ${discovered.fields[0].length}`);
  assert(discovered.fields[1] === true, `expected Complete=true from fixture, got ${discovered.fields[1]}`);
  assert(discovered.fields[2] === 2n, `expected generation threaded from summary, got ${discovered.fields[2]}`);
  assert(batch.tag === 2, `expected TestResultBatch tag 2, got ${batch.tag}`);
  assert(batch.fields[0].length === 2, `expected 2 results, got ${batch.fields[0].length}`);
  assert(batch.fields[1].tag === 0, `expected Fresh freshness tag 0, got ${batch.fields[1].tag}`);
});

run("parseSummary consumes suppressed-by-policy golden fixture", () => {
  const summary = parseSummary(readFixture("summary-with-suppressed-decision.json"));
  assert(summary.Total === 4, `expected Total=4, got ${summary.Total}`);
  assert(summary.LastDecision != null, "expected LastDecision to be present");
  assert(summary.LastDecision.Precision.tag === 4, `expected SuppressedByPolicy precision, got ${summary.LastDecision.Precision.tag}`);
  assert(summary.LastDecision.Trust.tag === 3, `expected Suppressed trust, got ${summary.LastDecision.Trust.tag}`);
  assert(summary.LastDecision.DeferredTests.length === 1, `expected 1 deferred test, got ${summary.LastDecision.DeferredTests.length}`);
});

run("parseSummary surfaces ready_zero_tests discovery state", () => {
  // Zero-test observability: the server's test_summary carries the
  // authoritative discovery state + generation; the client must surface it.
  const summary = parseSummary({
    Total: 0, Passed: 0, Failed: 0, Running: 0, Stale: 0, Disabled: 0,
    DiscoveryState: "ready_zero_tests",
    DiscoveryGeneration: 3,
  });
  assert(summary.DiscoveryState === "ready_zero_tests", `expected ready_zero_tests, got ${summary.DiscoveryState}`);
  assert(summary.DiscoveryGeneration === 3n || summary.DiscoveryGeneration === 3, `expected generation 3, got ${summary.DiscoveryGeneration}`);
  assert(summary.Total === 0, "zero tests stay observable as zero");
});

run("parseSummary defaults discovery state to discovering when absent", () => {
  // Old servers (pre-discovery-state) must not crash: default to discovering.
  const summary = parseSummary({ Total: 0, Passed: 0, Failed: 0, Running: 0, Stale: 0, Disabled: 0 });
  assert(summary.DiscoveryState === "discovering", `expected discovering default, got ${summary.DiscoveryState}`);
  assert(summary.DiscoveryGeneration === 0n || summary.DiscoveryGeneration === 0, "generation defaults to 0");
});

if (process.exitCode && process.exitCode !== 0) {
  process.exit(process.exitCode);
}
