/// How a save reaches the process that runs the user's code: by detour in the reload agent, by a metadata
/// delta in the worker, or not at all (a restart).
///
/// The source planner (`ReloadPlanning.planReload`) says what the edit IS. This says where it can go. An app
/// SageFs started with `run_app` runs in the worker, out of the agent's reach, so the detour cannot change
/// it; a metadata delta applied to the assembly the worker loaded can. The delta route is taken only when
/// the process was started for it (`MetadataDeltaMode.On`), and only for an edit the planner calls a patch
/// of function bodies: everything the planner already sends to a restart stays one, with the planner's more
/// specific reason.
namespace SageFs.Features

open SageFs.Features.MetadataDelta
open SageFs.Features.ReloadPlanning

/// Where one save goes.
[<RequireQualifiedAccess>]
type SaveRoute =
  /// Build the project and take the difference as a metadata delta. Carries the functions the source diff
  /// found changed, for the report.
  | ByMetadataDelta of functions: SourceDecl list
  /// The plan stands as the planner (adjusted for where the app runs) made it: patch in the agent, restart,
  /// or nothing to do.
  | PerPlan of ReloadPlan

[<RequireQualifiedAccess>]
module PatchRoute =

  let choose (placement: AppPlacement) (mode: MetadataDeltaMode) (plan: ReloadPlan) : SaveRoute =
    match placement, mode, plan with
    | AppPlacement.InWorkerProcess, MetadataDeltaMode.On, ReloadPlan.PatchFunctions (_ :: _ as functions) ->
      SaveRoute.ByMetadataDelta functions
    | _ -> SaveRoute.PerPlan(AppPlacement.adjust placement plan)
