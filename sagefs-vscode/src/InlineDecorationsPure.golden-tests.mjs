// WHY: issue #132's GIF. A two-binding block showed only `val a: int = 3` on a Windows machine. VS Code keeps only
// the FIRST LINE of a decoration's `contentText`: its decoration CSS code is `contentText.match(/^.*$/m)[0]` (read
// from VS Code 1.142's workbench.desktop.main.js), and `.` does not match a line feed, a carriage return, or the
// Unicode line and paragraph separators. A Windows daemon ends FSI's lines with CR LF, the renderer split on LF
// only, and every line kept its CR.
//
// This runs the REAL compiled renderers (fable-out/InlineDecorationsPure.js) on what a Windows daemon sends, and
// applies VS Code's own expression to what they produce: whatever VS Code would paint must be the whole text.
// Run `npm run compile` first. Run: node src/InlineDecorationsPure.golden-tests.mjs (also part of `npm run test:golden`).
import assert from "node:assert/strict";
import { fileURLToPath } from "node:url";
import path from "node:path";

const here = path.dirname(fileURLToPath(import.meta.url));
const Pure = await import(path.join(here, "../fable-out/InlineDecorationsPure.js"));

const LS = String.fromCharCode(0x2028); // Unicode line separator
const PS = String.fromCharCode(0x2029); // Unicode paragraph separator

/** What VS Code paints for a decoration's contentText (workbench.desktop.main.js, 1.142). */
const paintedByVsCode = (contentText) => contentText.match(/^.*$/m)[0];

const replies = {
  "windows, two bindings": "Result: val a: int = 3\r\nval b: int = 5\r\n",
  "unix, two bindings": "Result: val a: int = 3\nval b: int = 5\n",
  "old mac line ends": "Result: val a: int = 3\rval b: int = 5\r",
  "unicode separators": "Result: val a: int = 3" + LS + "val b: int = 5" + PS,
  "one line, windows": "Result: 5val it: unit = ()\r\n\r\n",
  "five lines, windows": "a\r\nb\r\nc\r\nd\r\ne\r\n",
};

for (const [name, reply] of Object.entries(replies)) {
  const rendered = Pure.renderInlineResult(undefined, reply);
  assert.ok(rendered, `${name}: a reply with text renders`);
  const painted = paintedByVsCode(rendered.ContentText);
  assert.equal(painted, rendered.ContentText, `${name}: VS Code paints the whole ghost text, not its first line (${JSON.stringify(rendered.ContentText)})`);
}

// The reporter's case, exactly: both bindings reach the screen.
const two = Pure.renderInlineResult(undefined, replies["windows, two bindings"]);
assert.match(paintedByVsCode(two.ContentText), /val a: int = 3.*val b: int = 5/, "both bindings are painted");

// WHY: issue #132, the second screenshot. Lines were joined with a box-drawing bar ("val a: int = 3  │  val b: int = 5"),
// which reads as noise. They are joined with a semicolon, and a result cut at four lines says how many it has.
assert.equal(two.ContentText, "  // → Result: val a: int = 3; val b: int = 5", "two bindings read as one plain line");
for (const [name, reply] of Object.entries(replies)) {
  assert.ok(!Pure.renderInlineResult(undefined, reply).ContentText.includes("│"), `${name}: no box-drawing bar in the ghost text`);
}
assert.equal(Pure.renderInlineResult(undefined, "a\nb\nc\nd\ne\nf").ContentText, "  // → a … (6 lines)", "a long result says how long it is");

// A diagnostic is first-line by design, and must still be painted whole.
const diagnostic = Pure.renderInlineDiagnostic("error FS0001: bad\r\nmore detail\r\n");
assert.equal(paintedByVsCode(diagnostic.ContentText), diagnostic.ContentText, "the diagnostic's ghost text is painted whole");
assert.match(diagnostic.ContentText, /error FS0001: bad$/, "and it is the first line");

// The last stop before VS Code keeps the indent and removes every kind of break.
assert.equal(Pure.asOneLine("  // → a\r\nb"), "  // → a b");
for (const text of ["x\ny", "x\ry", "x\r\ny", "x" + LS + "y", "x" + PS + "y"]) {
  assert.equal(paintedByVsCode(Pure.asOneLine(text)), Pure.asOneLine(text), `asOneLine paints whole for ${JSON.stringify(text)}`);
}

console.log("InlineDecorationsPure golden: all passed");
