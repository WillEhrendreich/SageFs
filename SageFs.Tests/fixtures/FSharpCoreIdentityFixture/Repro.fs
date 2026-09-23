/// GitHub #141/#142's own repro, verbatim: a project with NO package references (so it picks up the
/// SDK's implicit FSharp.Core, the config every normal user's project is in), compiled ahead of time so a
/// session evaluates the PROJECT's own compiled IL, not code FSI compiled itself. `useInTask` calls
/// `TaskBuilderBase.Using` — the exact overload the SDK's toolset FSharp.Core can lack even when it
/// reports the identical file/assembly version as the project's NuGet-restored copy (#141). The
/// directory helpers are #142's repro: AppContext.BaseDirectory and Assembly.Location should lead back to
/// this project's own build output, not the isolated host's.
module Repro

let useInTask () =
  task {
    use stream = new System.IO.MemoryStream()
    stream.WriteByte 1uy
    return stream.Length
  }
  |> fun t -> t.Result

let baseDirectory () = System.AppContext.BaseDirectory
let assemblyDirectory () = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)
let fsharpCoreLocation () = typeof<int option>.Assembly.Location
