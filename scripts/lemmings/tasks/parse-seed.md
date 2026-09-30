I have a small F# project in the current directory: a library called DemoEnv and an Expecto test project next to it. SageFs is set up for you as an MCP server called "sagefs".

One test is failing: "DemoEnv.parseSeed a negative integer yields None". Fix `SageFs.Samples.DemoEnv.parseSeed` so a negative integer string returns `None`, while "0" and positive integer strings still return `Some`.

Use SageFs to try the change out before you touch the file. When you're done, run the project's full test suite to confirm it passes, and clean up anything you started. Tell me briefly what you changed and what you ran.
