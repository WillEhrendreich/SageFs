import * as esbuild from "esbuild";

await esbuild.build({
  entryPoints: ["./fable-out/Extension.js"],
  bundle: true,
  outfile: "./dist/Extension.js",
  external: ["vscode"],
  format: "cjs",
  platform: "node",
  target: "node18",
  sourcemap: true,
  minify: false,
});

console.log("✅ esbuild: dist/Extension.js");
