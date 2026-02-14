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

module ElmLoop =
  /// Start the Elm loop with an initial model.
  /// Returns a dispatch function to send messages.
  let start (program: ElmProgram<'Model, 'Msg, 'Effect, 'Region>)
            (initialModel: 'Model) : 'Msg -> unit =
    let mutable model = initialModel
    let lockObj = obj ()

    let rec dispatch (msg: 'Msg) =
      let newModel, effects =
        lock lockObj (fun () ->
          let m, effs = program.Update msg model
          model <- m
          m, effs)

      let regions = program.Render newModel
      program.OnModelChanged newModel regions

      for effect in effects do
        Async.Start (program.ExecuteEffect dispatch effect)

    let regions = program.Render initialModel
    program.OnModelChanged initialModel regions
    dispatch
