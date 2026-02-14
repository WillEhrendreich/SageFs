module SageFs.Utils

//todo make instances
type ILogger =
  abstract member LogInfo: string -> unit
  abstract member LogDebug: string -> unit
  abstract member LogWarning: string -> unit
  abstract member LogError: string -> unit

module Configuration =
  open System
  open System.IO
  open System.Reflection

  let getEmbeddedFileAsString fileName (asm: Assembly) =
    task {
      let stream = asm.GetManifestResourceStream fileName
      if isNull stream then
        let available = asm.GetManifestResourceNames() |> String.concat ", "
        return failwithf "Embedded resource '%s' not found in %s. Available: %s" fileName asm.FullName available
      else
        use s = stream
        use reader = new StreamReader(s)
        return! reader.ReadToEndAsync()
    }

  let getBaseConfigString () =
    getEmbeddedFileAsString "SageFs.Core.base.fsx" (Assembly.GetExecutingAssembly())

  let getConfigDir () =
    let configDir =
      Environment.GetFolderPath Environment.SpecialFolder.ApplicationData
      |> fun s -> Path.Combine [| s; "SageFs" |]

    if not <| Directory.Exists configDir then
      do Directory.CreateDirectory configDir |> ignore

    configDir
