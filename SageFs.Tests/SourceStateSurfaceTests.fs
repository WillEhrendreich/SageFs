/// What an agent reads when the build a session runs is behind the files on disk. The state is `SourceState`, and it sits next to
/// `replFreshness` (the REPL behind the APP) and is not that: it is the DISK ahead of the BUILD. get_session_status carries it in every
/// shape it can answer in (ready, warming, faulted), list_sessions on the session's own entry. Each has a test here, through the
/// real tool member, over a project on disk, reading the text and the field an agent reads.
module SageFs.Tests.SourceStateSurfaceTests

open System.Text.Json
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open Microsoft.Extensions.Logging.Abstractions
open SageFs
open SageFs.McpTools
open SageFs.Server.McpTools
open SageFs.Tests.TestInfrastructure

/// The shared context, with the session's record changed by `change` and a worker that reports the project's build.
let private ctxOver (project: SourceStateFixtures.Project) (change: WorkerProtocol.SessionInfo -> WorkerProtocol.SessionInfo) : McpContext =
  let ctx = sharedCtx ()
  let ops = ctx.SessionOps
  let shaped (info: WorkerProtocol.SessionInfo) = change { info with ProjectRoles = [ SourceStateFixtures.classified project ] }
  { ctx with
      SessionOps =
        { ops with
            GetSessionInfo = fun id -> task { let! info = ops.GetSessionInfo id in return Option.map shaped info }
            GetAllSessions = fun () ->
              task {
                let! one = ops.GetSessionInfo (WorkerProtocol.SessionId.newId ())
                return one |> Option.map shaped |> Option.toList
              } }
      GetWarmupContext = Some (fun _ -> Task.FromResult (Some (SourceStateFixtures.warmup project))) }

/// The worker has no proxy yet, so a warming or faulted session resolves to its own status shape instead of the routable one.
let private unroutable (ctx: McpContext) : McpContext =
  { ctx with SessionOps = { ctx.SessionOps with GetProxy = fun _ -> Task.FromResult None } }

let private level (info: WorkerProtocol.SessionInfo) = info

let private rebuilding (info: WorkerProtocol.SessionInfo) =
  { info with Rebuild = LastRebuild.Latest (RebuildOutcome.InProgress (SourceStateFixtures.at -1)) }

let private statusOf (ctx: McpContext) : Task<JsonDocument> =
  task {
    let tools = SageFsTools(ctx, NullLogger<SageFsTools>.Instance)
    let! text = tools.get_session_status("", "", 0)
    return JsonDocument.Parse text
  }

[<Tests>]
let tests =
  // Sequenced for the same reason ReplFreshnessSurfaceTests is: these drive the one shared FSI actor.
  testSequenced <| testList "SourceState on every surface an agent reads" [

    testTask "WHY - get_session_status carries the source state as a field: in sync for a build that matches the files" {
      do! SourceStateFixtures.using (fun project -> task {
        use! status = statusOf (ctxOver project level)
        let state = status.RootElement.GetProperty("sourceState")
        state.GetProperty("state").GetString() |> Expect.equal "in sync" "InSync"
        state.GetProperty("filesChecked").GetInt32() |> Expect.equal "the project file and its source" 2 })
    }

    testTask "WHY - get_session_status names the files that changed after the build, so an agent can see an edit it has not built" {
      do! SourceStateFixtures.using (fun project -> task {
        SourceStateFixtures.editSource project
        use! status = statusOf (ctxOver project level)
        let state = status.RootElement.GetProperty("sourceState")
        state.GetProperty("state").GetString() |> Expect.equal "stale" "Stale"
        [ for f in state.GetProperty("changedFiles").EnumerateArray() -> f.GetProperty("path").GetString() ]
        |> Expect.equal "naming the edited file" [ project.Source ]
        state.GetProperty("message").GetString() |> Expect.stringContains "and what to do" "rebuild=true" })
    }

    testTask "WHY - the source state and the REPL's freshness are two fields: a stale source does not make the REPL behind the app" {
      do! SourceStateFixtures.using (fun project -> task {
        SourceStateFixtures.editSource project
        use! status = statusOf (ctxOver project level)
        status.RootElement.GetProperty("sourceState").GetProperty("state").GetString() |> Expect.equal "the disk is ahead of the build" "Stale"
        status.RootElement.GetProperty("replFreshness").GetProperty("state").GetString() |> Expect.equal "the REPL is not behind an app" "InSync" })
    }

    testTask "WHY - a rebuild in progress is Rebuilding on a Ready session: the old worker keeps serving, and the field says it is about to be replaced" {
      do! SourceStateFixtures.using (fun project -> task {
        use! status = statusOf (ctxOver project rebuilding)
        status.RootElement.GetProperty("sourceState").GetProperty("state").GetString() |> Expect.equal "rebuilding" "Rebuilding" })
    }

    testTask "WHY - the warming shape carries it too: a session that is building says Rebuilding, because that shape is exactly where a rebuild shows" {
      do! SourceStateFixtures.using (fun project -> task {
        let building (info: WorkerProtocol.SessionInfo) =
          { rebuilding info with Status = WorkerProtocol.SessionLifecycleStatus.Building ("rebuild", { Pid = 1; Port = None }) }
        use! status = statusOf (unroutable (ctxOver project building))
        status.RootElement.GetProperty("state").GetString() |> Expect.equal "the shape is the warming one" "WarmingUp"
        status.RootElement.GetProperty("sourceState").GetProperty("state").GetString() |> Expect.equal "rebuilding" "Rebuilding" })
    }

    testTask "WHY - the faulted shape carries it: a faulted session still says whether its last build is behind the files" {
      do! SourceStateFixtures.using (fun project -> task {
        let faulted (info: WorkerProtocol.SessionInfo) =
          { info with Status = WorkerProtocol.SessionLifecycleStatus.Faulted (WorkerProtocol.FaultReason.Reported "warmup failed") }
        use! status = statusOf (unroutable (ctxOver project faulted))
        status.RootElement.GetProperty("state").GetString() |> Expect.equal "the session is faulted" "Faulted"
        status.RootElement.TryGetProperty "sourceState" |> fst |> Expect.isTrue "and the field is there" })
    }

    testTask "WHY - list_sessions says it on the session's own entry, plainly when in sync and loudly when stale" {
      do! SourceStateFixtures.using (fun project -> task {
        let! inSyncText = SageFsTools(ctxOver project level, NullLogger<SageFsTools>.Instance).list_sessions()
        inSyncText |> Expect.stringContains "in sync is said" "Source: in sync with the build"
        SourceStateFixtures.editSource project
        let! staleText = SageFsTools(ctxOver project level, NullLogger<SageFsTools>.Instance).list_sessions()
        staleText |> Expect.stringContains "stale is loud" "STALE SOURCE"
        staleText |> Expect.stringContains "and names the file" "A.fs" })
    }
  ]
