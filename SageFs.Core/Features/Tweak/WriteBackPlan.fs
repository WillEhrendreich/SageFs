namespace SageFs.Features.Tweak

open System
open SageFs.Features.ReloadPlanning

/// The pure planning stage of a granular write-back. One eval can replace
/// several declarations; each becomes a named chunk the caller may accept or
/// refuse independently. This module never evaluates and never writes: it
/// answers "what would land where, and what does it need" from the source it is
/// given. The dependency edges are lexical (one declaration's replacement
/// mentions another's name), which is an honest over-approximation: a name that
/// appears in a comment or a string still creates an edge. Never guessing is
/// better than a wrong order, and the caller sees every edge before it accepts.
module WriteBackPlan =

  [<RequireQualifiedAccess>]
  type ChunkStatus =
    /// The splice was checked against the real file and can be applied.
    | Ready
    /// Some declaration this one mentions is not part of the plan.
    | NeedsDependency of names: string list
    /// The splice itself refused; the reason is the daemon's own guard text.
    | Refused of reason: string

  /// One candidate replacement, addressable by id so a caller can choose it.
  type Chunk =
    { Id: string
      Name: string
      File: string
      StartLine: int
      EndLine: int
      Replacement: string
      DependsOn: string list
      Status: ChunkStatus }

  type Plan =
    { File: string
      Source: string
      Chunks: Chunk list }

  /// Why an accepted set could not be ordered. A named reason, never a bare
  /// string: callers branch on the case and the text is a rendering detail.
  [<RequireQualifiedAccess>]
  type OrderRefusal =
    | UnknownChunk of id: string
    | Cycle of id: string

  module OrderRefusal =
    let describe (r: OrderRefusal) =
      match r with
      | OrderRefusal.UnknownChunk id -> sprintf "chunk '%s' is not in this plan" id
      | OrderRefusal.Cycle id -> sprintf "the dependencies of chunk '%s' form a cycle" id

  [<RequireQualifiedAccess>]
  type Refusal =
    | EmptyCode
    | NoFilePath
    | Unparseable of reason: string
    | NothingToWrite

  module Refusal =
    let describe (r: Refusal) =
      match r with
      | Refusal.EmptyCode -> "write_back plan: no code was supplied."
      | Refusal.NoFilePath -> "write_back plan: file_path is required."
      | Refusal.Unparseable reason -> sprintf "write_back plan: the submission cannot be parsed: %s" reason
      | Refusal.NothingToWrite -> "write_back plan: the submission declares nothing that can be written back."

  let private submission (code: string) =
    let trimmed = if isNull code then "" else code.Trim()
    if trimmed.EndsWith(";;", StringComparison.Ordinal) then trimmed.Substring(0, trimmed.Length - 2).TrimEnd()
    else trimmed

  /// The names this text mentions as identifiers. Deliberately simple: it is
  /// an over-approximation the caller can see, not a claim about symbol identity.
  let private mentions (names: string list) (text: string) =
    names
    |> List.filter (fun n -> n.Length > 1 && text.IndexOf(n, StringComparison.Ordinal) >= 0)

  /// One record instead of three bare strings: the three have different jobs
  /// (submission, address, bytes on disk) and a tuple of strings lets a caller
  /// slip them past each other.
  type PlanRequest =
    { Code: string
      File: string
      Source: string }

  let plan (r: PlanRequest) : Result<Plan, Refusal> =
    if String.IsNullOrWhiteSpace r.Code then Error Refusal.EmptyCode
    elif String.IsNullOrWhiteSpace r.File then Error Refusal.NoFilePath
    else
      let code, file, source = r.Code, r.File, r.Source
      let text = submission code
      match extractDecls text with
      | Error reason -> Error(Refusal.Unparseable reason)
      | Ok evalFile ->
        match evalFile.Decls |> List.filter (fun d -> d.Kind <> DeclKind.StartupCode) with
        | [] -> Error Refusal.NothingToWrite
        | evalDecls ->
          match extractDecls source with
          | Error reason -> Error(Refusal.Unparseable reason)
          | Ok fileDecls ->
            let names = fileDecls.Decls |> List.map (fun d -> d.Name)
            let chunks =
              evalDecls
              |> List.mapi (fun i decl ->
                // Ids are declaration names, stable within one plan.
                let dependsOn = mentions (names |> List.filter ((<>) decl.Name)) decl.Text
                let target =
                  fileDecls.Decls
                  |> List.filter (fun d -> d.Name = decl.Name)
                  |> List.tryHead
                let status =
                  match target with
                  | None -> ChunkStatus.Refused(sprintf "no declaration named %s in %s" decl.Name file)
                  | Some t ->
                    match EvalWriteBack.decide decl.Text (Some file) (Some t.StartLine) source with
                    | Ok _ -> ChunkStatus.Ready
                    | Error (EvalWriteBack.WriteBack.NotPersisted reason) -> ChunkStatus.Refused reason
                    | Error (EvalWriteBack.WriteBack.Written _) -> ChunkStatus.Refused "the splice decision returned a write verdict before any write"
                { Id = decl.Name
                  Name = decl.Name
                  File = file
                  StartLine = target |> Option.map (fun t -> t.StartLine) |> Option.defaultValue 0
                  EndLine = target |> Option.map (fun t -> t.EndLine) |> Option.defaultValue 0
                  Replacement = decl.Text
                  DependsOn = dependsOn
                  Status = status })
            Ok { File = file; Source = source; Chunks = chunks }

  /// Topological order of the accepted chunk ids. A cycle is refused by name
  /// rather than broken arbitrarily.
  let order (plan: Plan) (accepted: string list) : Result<string list, OrderRefusal> =
    let byId = plan.Chunks |> List.map (fun c -> c.Id, c) |> Map.ofList
    let rec visit (seen: Set<string>) (visiting: Set<string>) (id: string) : Result<Set<string> * string list, OrderRefusal> =
      match Map.tryFind id byId with
      | None -> Error(OrderRefusal.UnknownChunk id)
      | Some _ when Set.contains id seen -> Ok(seen, [])
      | Some _ when Set.contains id visiting -> Error(OrderRefusal.Cycle id)
      | Some c ->
        let visiting' = Set.add id visiting
        let deps = c.DependsOn |> List.filter (fun d -> List.contains d accepted)
        deps
        |> List.fold (fun (acc: Result<Set<string> * string list, OrderRefusal>) d ->
          match acc with
          | Error _ -> acc
          | Ok (s, a) ->
            match visit s visiting' d with
            | Error e -> Error e
            | Ok (s', a') -> Ok(s', a @ a')) (Ok(seen, []))
        |> Result.map (fun (s, a) -> Set.add id s, a @ [ id ])
    accepted
    |> List.fold (fun (acc: Result<Set<string> * string list, OrderRefusal>) id ->
      match acc with
      | Error _ -> acc
      | Ok (s, a) ->
        match visit s Set.empty id with
        | Error e -> Error e
        | Ok (s', a') -> Ok(s', a @ a')) (Ok(Set.empty, []))
    |> Result.map snd
