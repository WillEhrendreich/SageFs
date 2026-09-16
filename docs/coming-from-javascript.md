# 🟨 Coming from JavaScript / TypeScript?

If you've shipped `undefined is not a function` to production for years, F# offers a different deal: a type system that actually catches these bugs, no concept of `undefined`, and hot reload that's fast enough to feel instant.

You'll leave behind `node_modules` eating your disk, `any` creep in TypeScript, `undefined` vs `null` vs `""` vs `0` all being falsy, and webpack rebuilds that outlast your lunch break.

**What you'll notice right away:**
- `Option<'T>` means "might not exist," enforced by the compiler, so there's no runtime surprise
- `|>` pipelines work like `.filter().map().reduce()`, but for any function, not just array methods
- No `this` binding bugs; functions are just functions
- Fable compiles F# to clean JavaScript, and the SageFs VS Code extension is F# all the way down

**→ [Start here: `samples/from-javascript/hello.fsx`](../samples/from-javascript/hello.fsx)**

```fsharp
// JS/TS: type Shape = { kind: "circle"; r: number } | { kind: "rect"; w: number; h: number }
// F# (compiler checks exhaustiveness — no forgotten cases at runtime):
type Shape =
  | Circle    of radius: float
  | Rectangle of width: float * height: float

let area = function
  | Circle r          -> System.Math.PI * r * r
  | Rectangle (w, h) -> w * h
// Forget the Rectangle case? Warning. Add Triangle without updating area? Warning.
```
