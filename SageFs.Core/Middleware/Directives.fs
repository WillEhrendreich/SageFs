module SageFs.Middleware.Directives

#nowarn "57"

open System.IO
open FSharpPlus
open Fantomas.Core
open Fantomas.FCS.Syntax
open SageFs.AppState
open SageFs.Utils

module OpenDirective =
  let openedFileKey = "openedFiles"
  type OpenedFiles = string Set

  let openDirectiveMiddleware next (request, st) =
    let openDirectiveLines fileToOpen =
      let fileToOpen = Path.GetFullPath fileToOpen
      let file = File.ReadAllText fileToOpen

      let results = CodeFormatter.ParseAsync(false, file) |> Async.RunSynchronously
      let res, _ = results.[0]

      match res with
      | ParsedInput.ImplFile(ParsedImplFileInput(contents = contents)) ->
        match contents with
        | [ SynModuleOrNamespace(decls = codeLines; longId = l) ] ->
          let runOpen (l: LongIdent) =
            let path = l |> Seq.map _.idText |> Seq.toList |> String.concat "."
            $"open {path}"

          let chooseFn =
            function
            | SynModuleDecl.Open(target = target) ->
              match target with
              | SynOpenDeclTarget.ModuleOrNamespace(longId = l) -> Some <| runOpen l.LongIdent
              | SynOpenDeclTarget.Type(typeName = t) -> None //todo
            | _ -> None

          // Only include module open statement if it's not a temp/generated module name
          let moduleName = l |> Seq.map _.idText |> Seq.toList |> String.concat "."
          let moduleOpen = 
            if moduleName.StartsWith("Tmp") || moduleName = "" then 
              [] 
            else 
              [runOpen l]
          
          moduleOpen @ List.choose chooseFn codeLines
        | _ -> []
      | _ -> []

    let hasOpenedFile fileName =
      match st.Custom.TryFind openedFileKey with
      | None -> false
      | Some openedFileSet -> openedFileSet :?> OpenedFiles |> Set.contains fileName

    let addOpenedFile st fileName =
      let changeFn: obj option -> obj option =
        function
        | None -> Set.ofList [ fileName ] :> obj |> Some
        | Some set -> set :?> OpenedFiles |> Set.add fileName :> obj |> Some

      {
        st with
            Custom = Map.change openedFileKey changeFn st.Custom
      }

    match request with
    | { Code = code; Args = args } when args.ContainsKey "fileName" ->
      let fileName = args["fileName"] :?> string

      if fileName = null || hasOpenedFile fileName then
        next (request, st)
      else
        let lines = openDirectiveLines fileName
        let code = lines @ [ code ] |> String.concat "\n"

        for l in lines do
          st.Logger.LogDebug l

        let response, st = next ({ request with Code = code }, st)
        response, addOpenedFile st fileName
    | { Code = code } when code.StartsWith "#o" ->
      let commandWords = code.Split " "

      if commandWords.Length < 2 then
        next (request, st)
      else
        match commandWords[0] with
        | "#o"
        | "#open" ->
          let fileName = commandWords[1]
          let lines = openDirectiveLines fileName
          let code = String.concat "\n" lines

          for l in lines do
            st.Logger.LogDebug l

          let response, st = next ({ request with Code = code }, st)
          response, addOpenedFile st fileName
        | _ -> next (request, st)
    | _ -> next (request, st)

let viBindMiddleware next (request, st) =
  match request with
  | { Code = code } when code.StartsWith ":" ->
    let commandWords = code.Split(" ", System.StringSplitOptions.RemoveEmptyEntries)
    
    // Handle custom directives before converting to #
    match commandWords with
    | [| ":pwd" |] ->
      // Print current working directory
      let cwd = System.IO.Directory.GetCurrentDirectory()
      printfn "%s" cwd
      // Return empty response - output already printed
      { EvaluationResult = Ok ""; Diagnostics = [||]; EvaluatedCode = code; Metadata = Map.empty }, st
    | [| ":exec"; fileName |] | [| ":e"; fileName |] ->
      // Execute entire file contents directly (for top-level program files)
      let filePath = Path.GetFullPath fileName
      if File.Exists(filePath) then
        let fileContents = File.ReadAllText(filePath)
        st.Logger.LogInfo $"Executing: {filePath}"
        next ({ request with Code = fileContents }, st)
      else
        st.Logger.LogError $"File not found: {filePath}"
        { EvaluationResult = Error (System.IO.FileNotFoundException($"File not found: {filePath}")); 
          Diagnostics = [||]; 
          EvaluatedCode = code; 
          Metadata = Map.empty }, st
    | _ ->
      // Convert :cmd to #cmd for standard F# directives
      next (
        {
          request with
              Code = "#" + code.Substring 1
        },
        st
      )
  | _ -> next (request, st)
