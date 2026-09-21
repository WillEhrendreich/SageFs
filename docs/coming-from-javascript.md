# 🟨 Coming from JavaScript / TypeScript?

F# catches `undefined is not a function` at compile time, because there is no `undefined` in the language to begin with. And eval is fast enough that it feels like the browser console, not a build step.

You leave behind `node_modules` eating your disk, `any` creep in TypeScript, the `undefined`/`null`/`""`/`0` falsy mess, and webpack rebuilds that outlast your lunch break.

**What you'll notice right away:**
- `Option<'T>` means "might not exist," and the compiler makes you handle it. No runtime surprise three functions later.
- `|>` pipelines work like `.filter().map().reduce()`, but for any function, not just array methods.
- No `this` binding bugs. Functions are just functions.
- Fable compiles F# to JavaScript — the SageFs VS Code extension itself is written in F# and shipped through Fable, so this isn't a demo of the idea, it's the actual product.

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
