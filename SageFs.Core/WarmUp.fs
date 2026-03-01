module SageFs.WarmUp

/// Returns true if the FSI open error is benign — the module resolved but
/// can't be opened (e.g. RequireQualifiedAccess). Types are still accessible
/// via qualified paths, so this isn't a real failure.
let isBenignOpenError (errorMsg: string) : bool =
  errorMsg.Contains("RequireQualifiedAccess")

/// Opens names iteratively, retrying failures until convergence (no more progress).
/// opener: tries to open a name, returns Ok () on success or Error msg on failure.
/// Returns (succeeded, permanentFailures) where permanentFailures = (name, firstError).
/// First-round errors are preserved — not overwritten by cascade errors in later rounds.
let openWithRetry
  (maxRounds: int)
  (opener: string -> Result<unit, string>)
  (names: string list)
  : string list * (string * string) list =
  let firstErrors = System.Collections.Generic.Dictionary<string, string>()
  let rec loop round remaining acc =
    match round > maxRounds || List.isEmpty remaining with
    | true ->
      (acc, remaining |> List.map (fun n ->
        match firstErrors.TryGetValue(n) with
        | true, e -> n, e
        | _ -> n, "max retries exceeded"))
    | false ->
      let results =
        remaining
        |> List.map (fun name -> name, opener name)
      let succeeded =
        results
        |> List.choose (fun (n, r) ->
          match r with Ok () -> Some n | _ -> None)
      let failed =
        results
        |> List.choose (fun (n, r) ->
          match r with
          | Error e ->
            match firstErrors.ContainsKey(n) with
            | false -> firstErrors.[n] <- e
            | true -> ()
            Some (n, e)
          | _ -> None)
      match List.isEmpty succeeded with
      | true ->
        (acc, failed |> List.map (fun (n, _) ->
          n, (match firstErrors.TryGetValue(n) with true, e -> e | _ -> "unknown")))
      | false ->
        loop (round + 1) (failed |> List.map fst) (acc @ succeeded)
  loop 1 names []
