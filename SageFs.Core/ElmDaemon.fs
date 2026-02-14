module SageFs.ElmDaemon

/// Create an ElmProgram wired to real SageFs components.
/// The OnModelChanged callback is injected to allow different frontends.
let createProgram
  (deps: EffectDeps)
  (onModelChanged: SageFsModel -> RenderRegion list -> unit)
  : ElmProgram<SageFsModel, SageFsMsg, SageFsEffect, RenderRegion> =
  {
    Update = SageFsUpdate.update
    Render = SageFsRender.render
    ExecuteEffect = SageFsEffectHandler.execute deps
    OnModelChanged = onModelChanged
  }

/// Start the Elm loop with initial model and return dispatch function.
let start
  (deps: EffectDeps)
  (onModelChanged: SageFsModel -> RenderRegion list -> unit)
  : SageFsMsg -> unit =
  let program = createProgram deps onModelChanged
  ElmLoop.start program SageFsModel.initial

/// Dispatch a message and wait for the model to update.
/// Returns the model state after the dispatch has been processed.
let dispatchAndWait
  (dispatch: SageFsMsg -> unit)
  (getLatest: unit -> SageFsModel option)
  (waitForUpdate: int -> unit)
  (msg: SageFsMsg)
  (timeoutMs: int)
  : SageFsModel =
  dispatch msg
  waitForUpdate timeoutMs
  getLatest ()
  |> Option.defaultValue SageFsModel.initial
