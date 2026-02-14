module SageFs.Features.DiagnosticsStore

open SageFs.Features.Diagnostics

type T = Map<string, Diagnostic list>

let empty: T = Map.empty

let codeHash (code: string) =
  code.GetHashCode() |> sprintf "%08x"

let add (code: string) (diags: Diagnostic array) (store: T) : T =
  let key = codeHash code
  if Array.isEmpty diags then
    Map.remove key store
  else
    Map.add key (Array.toList diags) store

let forCode (code: string) (store: T) : Diagnostic list =
  let key = codeHash code
  Map.tryFind key store
  |> Option.defaultValue []

let all (store: T) : (string * Diagnostic list) list =
  Map.toList store

let allFlat (store: T) : Diagnostic list =
  store |> Map.values |> Seq.concat |> Seq.toList

let clear (_store: T) : T = Map.empty
