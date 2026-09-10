module SageFs.EntryPointDiscovery

open System
open System.IO
open System.Text.RegularExpressions
open Microsoft.Build.Evaluation
open Microsoft.Build.Construction

open FSharp.Compiler.SourceCodeServices
open FSharp.Compiler.AbstractIL
open FSharp.Compiler.TypeCheck
open FSharp.Compiler.SourceCodeServices.Impl
open FSharp.Compiler.ErrorCodes
open FSharp.Compiler.Symbols
open FSharp.Compiler.Check

/// Represents a discovered entry point candidate from source analysis.
type EntryPointCandidate = {
    /// The file containing the entry point
    FileName: string
    /// The function name (for [<EntryPoint>] functions) or "<top-level>" for top-level statements
    FunctionName: string
    /// Line number where the entry point is defined
    LineNumber: int
    /// The kind of entry point detected
    Kind: EntryPointKind
    /// Optional function signature for display (e.g., "main (argv: string[]) -> int")
    Signature: string option
}

/// The kind of entry point discovered.
type EntryPointKind =
    | Attribute    // [<EntryPoint>] attribute on a function
    | TopLevelStatements // File with no module/namespace (top-level statements)
    | PublicFunction // Public function that could be an entry point (fallback)

/// Result of entry point discovery - either found one, found multiple candidates, or none.
type EntryPointDiscoveryResult =
    | Found of EntryPointCandidate
    | Ambiguous of EntryPointCandidate list
    | NotFound of reason: string

/// Configuration for entry point discovery behavior.
type DiscoveryConfig = {
    /// Whether to consider public functions as entry point candidates (fallback)
    ConsiderPublicFunctions: bool
    /// Whether to look for [<EntryPoint>] attribute
    LookForEntryPointAttribute: bool
    /// Whether to consider top-level statements as entry points
    ConsiderTopLevelStatements: bool
    /// Additional hints from config files (e.g., .SageFs/config.fsx Run field)
    ConfigHints: string list
}

/// Default discovery configuration - tries all reasonable strategies.
let defaultDiscoveryConfig = {
    ConsiderPublicFunctions = true
    LookForEntryPointAttribute = true
    ConsiderTopLevelStatements = true
    ConfigHints = []
}

/// Extracts the project file path from a list of project references.
let getProjectFilePath (projects: string list) : string option =
    projects
    |> List.tryHead
    |> Option.map Path.GetFullPath

/// Discovers the entry point file using MSBuild metadata.
/// Returns the file that would be compiled as the entry point (last in compile order for Exe).
let discoverEntryPointFile (projectPath: string) : string option =
    try
        // Load the project to get compile items
        let project = ProjectCollection.GlobalProjectCollection.LoadProject(projectPath)

        // Get all Compile items
        let compileItems =
            project.Items
            |> Seq.filter (fun i -> i.ItemType = "Compile")
            |> Seq.map (fun i -> i.EvaluatedInclude)
            |> Seq.toList

        if compileItems.IsEmpty then
            None
        else
            // In F# projects, compile order matters - typically the last file is the entry point
            // for applications with top-level statements or [<EntryPoint>]
            Some (Path.GetFullPath(Path.Combine(Path.GetDirectoryName projectPath, List.last compileItems)))
    with
    | _ ->
        // Fallback: look for common entry point file names
        let directory = Path.GetDirectoryName projectPath
        let candidates =
            [ "Program.fs"
              "App.fs"
              "Main.fs" ]
            |> List.choose (fun name ->
                let fullPath = Path.Combine(directory, name)
                if File.Exists fullPath then Some fullPath else None)

        match candidates with
        | [single] -> Some single
        | _ -> List.tryLast candidates

/// Analyzes a source file to discover entry point candidates using F# Compiler Service.
let discoverEntryPointCandidates (filePath: string) (config: DiscoveryConfig) : EntryPointCandidate list =
    // Implementation would use F# Compiler Service to parse the file and look for:
    // 1. [<EntryPoint>] attributes on functions
    // 2. Top-level statements (no module/namespace wrapping)
    // 3. Public functions as fallback
    //
    // For now, returning empty list as placeholder - this would be implemented with FCS parsing
    []

/// Main entry point discovery function - combines MSBuild metadata with source analysis.
/// Returns either a single entry point, multiple candidates (ambiguous), or none found.
let discoverEntryPoint (projectPath: string) (config: DiscoveryConfig) : EntryPointDiscoveryResult =
    // Step 1: Use MSBuild to determine if this is an executable project and find the entry point file
    let isExecutable =
        try
            let project = ProjectCollection.GlobalProjectCollection.LoadProject(projectPath)
            match project.GetPropertyValue("OutputType") with
            | "Exe" -> true
            | _ -> false
        with
        | _ -> false

    if not isExecutable then
        NotFound "Project is not an executable (OutputType != Exe)"
    else
        // Step 2: Find the entry point file using MSBuild compile order
        let entryPointFileOption = discoverEntryPointFile projectPath

        match entryPointFileOption with
        | None ->
            NotFound "Could not determine entry point file from project"
        | Some entryPointFile ->
            // Step 3: Analyze the entry point file for actual entry point candidates
            let candidates = discoverEntryPointCandidates entryPointFile config

            match candidates with
            | [single] -> Found single
            | multiple when List.length multiple > 1 ->
                // Multiple candidates found - present them to user for disambiguation
                Ambiguous multiple
            | [] ->
                // No candidates found - try to provide helpful hints
                let hints =
                    if config.ConfigHints.Length > 0 then
                        sprintf "Check config hints: %s" (String.Join(", ", config.ConfigHints))
                    else
                        "No [<EntryPoint>] attribute, top-level statements, or public functions found"

                NotFound hints