This is a copy of the SageFs source tree. SageFs is set up for you as an MCP server called "sagefs".

I need to know what `SageFs.RingBuffer` (in `SageFs.Core/RingBuffer.fs`) actually does at runtime, not what the comments say. Make a buffer with capacity 4, push 10, 20, 30, 40, 50, 60 in that order, and find out:

- what `toList` returns
- what `tryGet 2` returns
- what `evictedCount` returns
- which .NET major version the F# session itself is running on (`System.Environment.Version.Major`)

Use SageFs to find these out. Don't change any source files. Write the answers to `ANSWER.md` in the project root, one line each, in the form `name = value`, using the names `toList`, `tryGet 2`, `evictedCount` and `runtimeMajor`. Clean up anything you started, then tell me briefly what you did.
