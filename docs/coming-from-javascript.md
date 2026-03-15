# 🟨 Coming from JavaScript / TypeScript?

You've been shipping `undefined is not a function` to production for years, and you've made peace with it. F# offers you something radical: a language where the type system is actually on your side, where `undefined` is not a concept, and where hot reload is so fast it feels like cheating.

Pain you're leaving behind: `node_modules` eating your disk, `any` creep in TypeScript, `undefined` vs `null` vs `""` vs `0` all being falsy, and webpack rebuilds that take longer than your lunch break.

**What you'll love immediately:**
- `Option<'T>` means "might not exist" — compiler-enforced, no runtime surprise
- `|>` pipelines are `.filter().map().reduce()` but for *any* function, not just array methods
- No `this` binding bugs — functions are just functions
- Fable compiles F# to clean JavaScript — the SageFs VS Code extension is F# all the way down

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
