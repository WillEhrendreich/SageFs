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
          try
            let m, effs = program.Update msg model
            model <- m
            m, effs
          with ex ->
            eprintfn "[ElmLoop] Update threw: %s" ex.Message
            model, [])

      let regions =
        try program.Render newModel
        with ex ->
          eprintfn "[ElmLoop] Render threw: %s" ex.Message
          lock lockObj (fun () -> latestRegions)

      lock lockObj (fun () -> latestRegions <- regions)

      try program.OnModelChanged newModel regions
      with ex -> eprintfn "[ElmLoop] OnModelChanged threw: %s" ex.Message

      for effect in effects do
        Async.Start (async {
          try do! program.ExecuteEffect dispatch effect
          with ex -> eprintfn "[ElmLoop] Effect threw: %s" ex.Message
        })

    let regions =
      try program.Render initialModel
      with ex ->
        eprintfn "[ElmLoop] Initial Render threw: %s" ex.Message
        []
    latestRegions <- regions
    try program.OnModelChanged initialModel regions
    with ex -> eprintfn "[ElmLoop] Initial OnModelChanged threw: %s" ex.Message

    { Dispatch = dispatch
      GetModel = fun () -> lock lockObj (fun () -> model)
      GetRegions = fun () -> lock lockObj (fun () -> latestRegions) }
