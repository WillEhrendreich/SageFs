namespace SageFs

open System
open System.Diagnostics
open System.IO
open System.Text.Json

type DaemonInfo = {
  Pid: int
  Port: int
  StartedAt: DateTime
  WorkingDirectory: string
  Version: string
}

module DaemonState =

  let SageFsDir =
    let home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
    Path.Combine(home, ".SageFs")

  let daemonJsonPath = Path.Combine(SageFsDir, "daemon.json")

  let private jsonOptions =
    JsonSerializerOptions(
      PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
      WriteIndented = true
    )

  let isProcessAlive (pid: int) =
    try
      let p = Process.GetProcessById(pid)
      not p.HasExited
    with
    | :? ArgumentException -> false
    | :? InvalidOperationException -> false

  let serializeInfo (info: DaemonInfo) =
    JsonSerializer.Serialize(info, jsonOptions)

  let deserializeInfo (json: string) =
    JsonSerializer.Deserialize<DaemonInfo>(json, jsonOptions)

  let writeToPath (path: string) (info: DaemonInfo) =
    let dir = Path.GetDirectoryName path
    Directory.CreateDirectory(dir) |> ignore
    let json = serializeInfo info
    let tmpPath = path + ".tmp"
    File.WriteAllText(tmpPath, json)
    File.Move(tmpPath, path, overwrite = true)

  let clearPath (path: string) =
    try File.Delete path with _ -> ()

  let readFromPath (path: string) : DaemonInfo option =
    if not (File.Exists path) then None
    else
      try
        let json = File.ReadAllText(path)
        let info = deserializeInfo json
        if isProcessAlive info.Pid then Some info
        else
          clearPath path
          None
      with _ ->
        clearPath path
        None

  let write (info: DaemonInfo) = writeToPath daemonJsonPath info
  let read () = readFromPath daemonJsonPath
  let clear () = clearPath daemonJsonPath
