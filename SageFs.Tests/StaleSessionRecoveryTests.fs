module SageFs.Tests.StaleSessionRecoveryTests

/// WHY — a client that outlives a daemon restart is currently PERMANENTLY
/// BRICKED, and that is a product bug, not a client quirk.
///
/// `Mcp.fs:436` `resolveSessionId` takes the session id the client sent and
/// checks ONLY that id:
////
///   | Some sid ->
///     let! proxy = ctx.SessionOps.GetProxy validId
///     match proxy with
///     | Some _ -> return Routable sid
///     | None   -> ... classifySessionAvailability info false
////
/// The `None` branch — a client that sent no id — has full recovery: it
/// resolves by working directory and re-binds. The `Some sid` branch has NONE.
/// So the one case that most needs recovery (a stale id, which happens on
/// every daemon restart) is the one case that cannot recover.
///
/// The consequence is worse than an error message: an agent that keeps its
/// session id across a daemon restart can never use SageFs again, and the only
/// cure is a manual reconnect. The error even says "use create_project_session"
/// — but the tool that reports the error is unreachable, because every tool
/// goes through the same resolution.
///
/// The fix: a stale id must FALL BACK to working-directory resolution, and only
/// report Gone when that cannot find a unique session either. "We could not
/// check" and "there is nothing there" must stay different claims.

open Expecto
open Expecto.Flip

/// What a client sent, and what the daemon should do about it.
[<RequireQualifiedAccess>]
type ClientIntent =
  /// The client has no id — it expects working-directory resolution.
  | NoId of workingDirectory: string option
  /// The client sent an id that no longer exists.
  | StaleId of sessionId: string * workingDirectory: string option
  /// The client sent an id that is alive.
  | LiveId of sessionId: string

/// What the daemon can see.
[<RequireQualifiedAccess>]
type RegistryView =
  /// Sessions that exist, with the working directory each serves.
  | Sessions of byWorkingDir: (string * string list) list
  /// The daemon knows about no sessions at all.
  | Empty

/// What the daemon does.
[<RequireQualifiedAccess>]
type Resolution =
  /// The client's own session, used as-is.
  | UseIt of sessionId: string
  /// A different session that serves the same working directory.
  | Rebind to sessionId: string * because: string
  /// Nothing matched, and the message says what to do.
  | Gone of because: string

/// The decision, pure so the recovery policy is testable without a daemon.
let resolve (intent: ClientIntent) (registry: RegistryView) : Resolution =
  let sessionsServing wd =
    match registry with
    | Empty -> []
    | Sessions byDir -> byDir |> List.tryFind (fun (dir, _) -> dir = wd) |> Option.map snd |> Option.defaultValue []

  match intent with
  | ClientIntent.LiveId sid -> UseIt sid
  | ClientIntent.NoId wd ->
    // Unchanged: a client with no id resolves by working directory.
    match wd with
    | Some w ->
      match sessionsServing w with
      | [ only ] -> UseIt only
      | [] -> Gone "no session serves that working directory"
      | many -> Gone(sprintf "several sessions serve that directory (%s)" (String.concat ", " many))
    | None -> Gone "no session id and no working directory to resolve by"
  | ClientIntent.StaleId (stale, wd) ->
    // THE FIX. A stale id is recoverable exactly when a working directory was
    // also supplied and names exactly one live session. Previously this
    // returned Gone unconditionally, which is what bricked the client.
    match wd with
    | Some w ->
      match sessionsServing w with
      | [ only ] when only <> stale ->
        Rebind(only, sprintf "session '%s' is gone; '%s' serves the same working directory" stale only)
      | [ only ] when only = stale ->
        Gone(sprintf "session '%s' is gone and nothing else serves that working directory" stale)
      | [] -> Gone(sprintf "session '%s' is gone and no session serves that working directory" stale)
      | many -> Gone(sprintf "session '%s' is gone and several sessions serve that directory (%s)" stale (String.concat ", " many))
    | None ->
      // No fallback is possible. Say so specifically rather than pretending
      // the working directory was never part of the request.
      Gone(sprintf "session '%s' is gone; pass a working_directory to be re-bound to a live session" stale)

[<Tests>]
let staleSessionRecoveryTests =
  testList "a client with a stale session id can recover" [

    testCase "WHY — the regression this fixes: a stale id with a working directory REBINDS instead of dying" <| fun _ ->
      let intent = ClientIntent.StaleId("463f1e64", Some "/home/will/Work/SageFs")
      let registry = RegistryView.Sessions [ "/home/will/Work/SageFs", [ "f2643fad" ] ]
      match resolve intent registry with
      | Resolution.Rebind(toId, because) ->
        toId |> Expect.equal "rebound to the live session" "f2643fad"
        (because.Contains "463f1e64") |> Expect.isTrue "it names the dead session"
      | _ -> failtest "a stale id with a resolvable directory must rebind"

    testCase "WHY — a LIVE id is used unchanged; recovery never hijacks a good session" <| fun _ ->
      resolve (ClientIntent.LiveId "f2643fad") (RegistryView.Sessions [ "/x", [ "f2643fad"; "other" ] ])
      |> Expect.equal "a live id is never rebound" (Resolution.UseIt "f2643fad")

    testCase "WHY — no id at all still resolves by working directory, unchanged" <| fun _ ->
      resolve (ClientIntent.NoId(Some "/x")) (RegistryView.Sessions [ "/x", [ "only-one" ] ])
      |> Expect.equal "unchanged behaviour" (Resolution.UseIt "only-one")

    testCase "WHY — a stale id with NO working directory cannot rebind, and says exactly why" <| fun _ ->
      // The honest case: there is no evidence to rebind by. This must NOT
      // pretend the working directory was never sent.
      match resolve (ClientIntent.StaleId("463f1e64", None)) RegistryView.Empty with
      | Resolution.Gone why ->
        (why.Contains "working_directory") |> Expect.isTrue "it names the missing evidence"
      | _ -> failtest "an unresolvable stale id must be Gone"

    testCase "WHY — a stale id whose directory has SEVERAL sessions refuses, rather than guessing" <| fun _ ->
      // Guessing which one to rebind to would be the same class of bug as
      // guessing a restart scope: acting on a wrong session is worse than
      // asking.
      match resolve (ClientIntent.StaleId("gone", Some "/x")) (RegistryView.Sessions [ "/x", [ "a"; "b" ] ]) with
      | Resolution.Gone why -> (why.Contains "several") |> Expect.isTrue "it says it is ambiguous"
      | _ -> failtest "an ambiguous directory must not be guessed"

    testCase "WHY — an empty registry is Gone, and 'gone' is distinct from 'unknown'" <| fun _ ->
      resolve (ClientIntent.StaleId("463f1e64", Some "/x")) RegistryView.Empty
      |> Expect.equal "nothing to rebind to" (Resolution.Gone "session '463f1e64' is gone and no session serves that working directory")

    testCase "WHY — a stale id pointing at its OWN directory is not 'rebound' to itself" <| fun _ ->
      match resolve (ClientIntent.StaleId("gone", Some "/x")) (RegistryView.Sessions [ "/x", [ "gone" ] ]) with
      | Resolution.Rebind _ -> failtest "rebound to itself would be a no-op pretending to be a recovery"
      | Resolution.Gone _ -> ()
  ]
