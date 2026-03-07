# F# Koans — SageFs Edition

22 progressive exercises teaching F# from scratch. Adapted from
[FSharpKoans](https://github.com/ChrisMarinos/FSharpKoans) by Chris Marinos (MIT).

## Quick Start

```bash
cd SageFs.Samples.Koans
dotnet run
```

You should see:

```
EXPECTO! 162 tests run — 162 passed, 0 failed. Success!
```

## Using with SageFs (recommended)

For the best learning experience, use SageFs for live feedback:

```bash
cd SageFs.Samples.Koans
sagefs watch .
```

Then open any `.fs` file in your editor (VS Code, Neovim, or TUI):

1. **Alt+Enter** on any expression to see its value inline
2. **Edit** a test — change a value and save
3. **See** ✓/✗ gutter markers update in < 200ms

No terminal. No `dotnet run`. Just instant feedback.

## The Koan Journey

| # | File | Topic |
|---|------|-------|
| 01 | `AboutAsserts.fs` | Meet Expecto (SageFs's test runner) |
| 02 | `AboutLet.fs` | `let` bindings and type inference |
| 03 | `AboutFunctions.fs` | Defining and calling functions |
| 04 | `AboutUnit.fs` | The unit type (F#'s void) |
| 05 | `AboutOrderOfEvaluation.fs` | Parentheses and the `<\|` operator |
| 06 | `AboutTuples.fs` | Grouping values with tuples |
| 07 | `AboutStrings.fs` | String operations and interpolation |
| 08 | `AboutBranching.fs` | `if` expressions and pattern matching |
| 09 | `AboutLists.fs` | F# lists (the bread and butter) |
| 10 | `AboutPipelining.fs` | The `\|>` operator (you'll love this) |
| 11 | `AboutArrays.fs` | .NET arrays |
| 12 | `AboutLooping.fs` | `for` and `while` loops |
| 13 | `MoreAboutFunctions.fs` | Lambdas, currying, partial application |
| 14 | `AboutDotNetCollections.fs` | Dictionary, List\<T\>, Seq |
| 15 | `AboutStockExample.fs` | Apply everything to real data 📊 |
| 16 | `AboutRecordTypes.fs` | Records (named product types) |
| 17 | `AboutOptionTypes.fs` | Option\<'T\> (no more null!) |
| 18 | `AboutDiscriminatedUnions.fs` | Discriminated unions (sum types) ⭐ |
| 19 | `AboutModules.fs` | Organizing code with modules |
| 20 | `AboutClasses.fs` | OOP classes in F# |
| 21 | `AboutFiltering.fs` | filter / find / choose / pick |
| 22 | `GraduationGuide.fs` | What comes next? Real projects! |

## Exercise Mode

The project contains **solved** answers so all tests pass. To use the koans
as exercises:

1. Open any `.fs` file
2. Replace the solved values with `__` (the placeholder):
   ```fsharp
   // Change this:
   (1 + 1) |> Expect.equal "1 + 1 should equal 2" 2
   // To this:
   (1 + 1) |> Expect.equal "1 + 1 should equal 2" __
   ```
3. Add the placeholder definition at the top of the file:
   ```fsharp
   let inline __<'T> : 'T = failwith "Seek wisdom by filling in the __"
   ```
4. Save — SageFs shows 🔴 markers for failing tests
5. Fill in the blanks to turn them 🟢

Or use the original `.fsx` scripts in this directory — they already have
the `__` placeholders ready for you.

## Why SageFs Makes This More Fun

| Original Koans | SageFs Edition |
|-----------------|----------------|
| NUnit `AssertEquality` | Expecto.Flip pipelines |
| `dotnet run` ~3s/cycle | Save → gutter marker < 200ms |
| One failure halts all | All tests run, each shows ✓/✗ |
| Scroll terminal output | Gutter arrow jumps to failure |
| Custom `[<Koan>]` runner | Industry-standard Expecto |
| Edit → build → run | Alt+Enter for instant results |

## License

These exercises are adapted from FSharpKoans by Chris Marinos.
See [LICENSE-FSharpKoans](LICENSE-FSharpKoans) for the original MIT license.
