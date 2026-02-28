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
  open System.Diagnostics

  /// Start the Elm loop with an initial model.
  /// Returns an ElmRuntime with dispatch, model reader, and region reader.
  let start (program: ElmProgram<'Model, 'Msg, 'Effect, 'Region>)
            (initialModel: 'Model) : ElmRuntime<'Model, 'Msg, 'Region> =
    let mutable model = initialModel
    let mutable latestRegions = []
    let lockObj = obj ()
    let sw = Stopwatch()

    let rec dispatch (msg: 'Msg) =
      Instrumentation.elmDispatchCount.Add(1L)
      sw.Restart()
      let snapshot, effects =
        lock lockObj (fun () ->
          try
            let m, effs = program.Update msg model
            model <- m
            m, effs
          with ex ->
            eprintfn "[ElmLoop] Update threw: %s" ex.Message
            model, [])
      sw.Stop()
      let msgType = msg.GetType().Name
      Instrumentation.elmloopUpdateMs.Record(sw.Elapsed.TotalMilliseconds, System.Collections.Generic.KeyValuePair("msg_type", msgType :> obj))

      sw.Restart()
      let regions =
        try program.Render snapshot
        with ex ->
          eprintfn "[ElmLoop] Render threw: %s" ex.Message
          lock lockObj (fun () -> latestRegions)
      sw.Stop()
      Instrumentation.elmloopRenderMs.Record(sw.Elapsed.TotalMilliseconds)

      lock lockObj (fun () -> latestRegions <- regions)

      sw.Restart()
      try program.OnModelChanged snapshot regions
      with ex -> eprintfn "[ElmLoop] OnModelChanged threw: %s" ex.Message
      sw.Stop()
      Instrumentation.elmloopCallbackMs.Record(sw.Elapsed.TotalMilliseconds)

      if not effects.IsEmpty then
        Instrumentation.elmloopEffectsSpawned.Add(int64 effects.Length)
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
