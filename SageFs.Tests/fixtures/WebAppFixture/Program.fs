module WebAppFixture.Program

open System
open System.Threading

[<EntryPoint>]
let main args =
  let port =
    args
    |> Array.tryPick (fun a ->
      if a.StartsWith("--port=", StringComparison.Ordinal) then
        match Int32.TryParse(a.Substring("--port=".Length)) with
        | true, p -> Some p
        | _ -> None
      else None)
    |> Option.defaultValue 5000
  // App.run returns immediately (the server runs on a background task);
  // block forever so the process stays alive as a standalone exe.
  App.run port
  Thread.Sleep(Timeout.Infinite)
  0
