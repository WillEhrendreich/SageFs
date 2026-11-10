namespace SageFs

open System
open System.IO

/// Whether the settings file could be read at all. An absent file is readable (it holds no layouts).
[<RequireQualifiedAccess>]
type FileReading =
  | Readable
  /// The file is there and is not a settings layer, or could not be opened; the words say which.
  | Unreadable of reason: string

/// What reading the saved layouts found: the layouts, and whether the file could be used (when it could not, every workflow
/// shows its default, and the menu says why).
type PaneLayoutRead =
  { Book: LayoutBook
    Reading: FileReading }

/// Where the pane layouts are kept: the global settings layer file (`<SageFsDir>/settings.json`), one key per workflow
/// (`dashboard.layout.<workflow>`), through `SettingsStore`, so a layout is written the way every saved setting is (atomic
/// tmp-and-move, never a half file, a hand-edited file kept beside it as `.corrupt` before it is replaced).
///
/// The keys are not rows of the Settings page: a layout is edited by moving panels on the dashboard, and a text row of
/// `sidebar=a,b;dock=c` would be a second, worse way to do it. The decisions (what an edit writes, what text means) are
/// `PaneLayout`'s and are pure; this file only reads, writes and remembers.
[<RequireQualifiedAccess>]
module PaneLayoutStore =

  /// The workflow a session's layout belongs to.
  let ofSessionWorkflow (workflow: WorkflowTypes.SessionWorkflow) : LayoutWorkflow =
    match workflow with
    | WorkflowTypes.SessionWorkflow.Interactive -> LayoutWorkflow.Repl
    | WorkflowTypes.SessionWorkflow.LiveTesting -> LayoutWorkflow.LiveTesting
    | WorkflowTypes.SessionWorkflow.HotReload _ -> LayoutWorkflow.HotReload

  /// One write, as a change to the layer file's map.
  let layerChangeOf (write: LayoutWrite) : SettingsStore.LayerChange =
    match write with
    | LayoutWrite.Save(key, text) -> SettingsStore.LayerChange.SetValue(key, text)
    | LayoutWrite.Clear key -> SettingsStore.LayerChange.ClearValue key

  /// One write applied to a layer map, by the same function the file store applies.
  let applyWrite (write: LayoutWrite) (layer: Map<string, string>) : Map<string, string> =
    SettingsStore.applyChange (layerChangeOf write) layer

  /// The file's layout keys as the book they make.
  let readBook (path: string) : PaneLayoutRead =
    match SettingsStore.readLayerChecked path with
    | SettingsStore.LayerRead.Layer layer -> { Book = LayoutBook.read layer; Reading = FileReading.Readable }
    | SettingsStore.LayerRead.Unreadable reason -> { Book = LayoutBook.empty; Reading = FileReading.Unreadable reason }

  /// What identifies the file's content cheaply: present or not, when it was written and how long it is.
  type Stamp =
    | Absent
    | Present of writtenAt: DateTime * length: int64

  let stampOf (path: string) : Stamp =
    try
      match File.Exists path with
      | false -> Absent
      | true ->
        let info = FileInfo path
        Present(info.LastWriteTimeUtc, info.Length)
    with
    | :? IOException
    | :? UnauthorizedAccessException -> Absent

  /// The layouts of one data directory, read from the file and remembered until the file changes (a push per tab reads this, so
  /// it costs one stat). Every edit goes through `Edit`, which reads the file again, decides on what it holds NOW and writes,
  /// under one lock, so two tabs editing at once cannot drop each other's change.
  type Store(globalDir: string) =
    let path = SettingsStore.globalPath globalDir
    let gate = obj ()
    let mutable remembered : (Stamp * PaneLayoutRead) option = None

    member _.Path : string = path

    member _.Read() : PaneLayoutRead =
      lock gate (fun () ->
        let stamp = stampOf path
        match remembered with
        | Some(known, read) when known = stamp -> read
        | Some _
        | None ->
          let read = readBook path
          remembered <- Some(stamp, read)
          read)

    /// Carry out one edit against the file as it is now. The writes are `LayoutBook.writesFor`'s; the first one that fails
    /// ends the edit with its error.
    member _.Edit(edit: BookEdit) : Result<unit, ConfigError> =
      lock gate (fun () ->
        remembered <- None
        let layer =
          match SettingsStore.readLayerChecked path with
          | SettingsStore.LayerRead.Layer layer -> layer
          | SettingsStore.LayerRead.Unreadable _ -> Map.empty
        let outcome =
          LayoutBook.writesFor layer edit
          |> List.fold
            (fun outcome write -> outcome |> Result.bind (fun () -> SettingsStore.update path (layerChangeOf write)))
            (Result.Ok())
        remembered <- None
        outcome)
