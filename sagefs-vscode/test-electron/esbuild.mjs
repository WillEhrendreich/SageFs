// Bundles the Fable output for this proof suite into CommonJS, matching
// sagefs-vscode/esbuild.mjs's own approach for the shipped extension.
// Two entry points: the launcher (run directly via `node`, needs
// @vscode/test-electron resolvable from node_modules) and the extension-host
// suite (loaded by VS Code's own extension host via `extensionTestsPath`,
// needs `vscode` resolvable there instead).
import * as esbuild from "esbuild";

await esbuild.build({
  entryPoints: ["./test-electron-out/Launcher.js"],
  bundle: true,
  outfile: "./test-electron-dist/launcher.cjs",
  external: ["@vscode/test-electron"],
  format: "cjs",
  platform: "node",
  target: "node18",
  sourcemap: true,
  minify: false,
});

await esbuild.build({
  entryPoints: ["./test-electron-out/ExtensionHostSuite.js"],
  bundle: true,
  outfile: "./test-electron-dist/suite.cjs",
  external: ["vscode"],
  format: "cjs",
  platform: "node",
  target: "node18",
  sourcemap: true,
  minify: false,
});

console.log("✅ esbuild: test-electron-dist/launcher.cjs, test-electron-dist/suite.cjs");
