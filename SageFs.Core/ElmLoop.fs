namespace SageFs

/// Core Elm Architecture types — the contract every frontend depends on
type Update<'Model, 'Msg, 'Effect> =
  'Msg -> 'Model -> 'Model * 'Effect list

type Render<'Model, 'Region> =
  'Model -> 'Region list

type EffectHandler<'Msg, 'Effect> =
  ('Msg -> unit) -> 'Effect -> Async<unit>

/// An Elm Architecture program definition
type ElmProgram<'Model, 'Msg, 'Effect, 'Region> = {
  Update: Update<'Model, 'Msg, 'Effect>
  Render: Render<'Model, 'Region>
  ExecuteEffect: EffectHandler<'Msg, 'Effect>
  OnModelChanged: 'Model -> 'Region list -> unit
}

/// The running Elm loop — dispatch messages and read current state.
type ElmRuntime<'Model, 'Msg, 'Region> = {
  Dispatch: 'Msg -> unit
  GetModel: unit -> 'Model
  GetRegions: unit -> 'Region list
}

module ElmLoop =
  /// Start the Elm loop with an initial model.
  /// Returns an ElmRuntime with dispatch, model reader, and region reader.
  let start (program: ElmProgram<'Model, 'Msg, 'Effect, 'Region>)
            (initialModel: 'Model) : ElmRuntime<'Model, 'Msg, 'Region> =
    let mutable model = initialModel
    let mutable latestRegions = []
    let lockObj = obj ()

    let rec dispatch (msg: 'Msg) =
      let newModel, effects =
        lock lockObj (fun () ->
          let m, effs = program.Update msg model
          model <- m
          m, effs)

      let regions = program.Render newModel
      lock lockObj (fun () -> latestRegions <- regions)
      program.OnModelChanged newModel regions

      for effect in effects do
        Async.Start (program.ExecuteEffect dispatch effect)

    let regions = program.Render initialModel
    latestRegions <- regions
    program.OnModelChanged initialModel regions

    { Dispatch = dispatch
      GetModel = fun () -> lock lockObj (fun () -> model)
      GetRegions = fun () -> lock lockObj (fun () -> latestRegions) }
