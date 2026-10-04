/// The real disk behind the nudge door's `FileSteps`: System.IO, with fsync where
/// the protocol needs a step to be durable when it returns.
module SageFs.Features.Tweak.NudgeFs

open SageFs.Features.Tweak.NudgeIo

/// The production steps.
let steps : FileSteps =
  { ReadBytes = fun _ -> failwith "not built yet"
    KindOf = fun _ -> failwith "not built yet"
    MakeDirectory = fun _ -> failwith "not built yet"
    WriteTemp = fun _ _ -> failwith "not built yet"
    Rename = fun _ _ -> failwith "not built yet"
    Append = fun _ _ -> failwith "not built yet"
    Discard = fun _ -> failwith "not built yet" }

/// Where the journal for `file` in `session` lives under `tweaksDir`. One journal
/// per file per session, named by a hash of the file's path.
let journalPathFor (tweaksDir: string) (session: string) (file: string) : string = failwith "not built yet"
