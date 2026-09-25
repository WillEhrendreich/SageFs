module SageFs.Server.DaemonTelemetry

open System
open System.Collections.Generic
open System.Diagnostics

type ProcessTelemetry = {
    ProcessId: int
    Role: string
    ResidentBytes: int64
    CpuTime: TimeSpan
    CpuPercent: float
}

type Snapshot = {
    SampledAt: DateTimeOffset
    AggregateResidentBytes: int64
    AggregateCpuPercent: float
    Processes: ProcessTelemetry list
}

type private Previous = DateTimeOffset * TimeSpan
type private State = {
    mutable Previous: Dictionary<int, Previous>
    mutable Current: Snapshot option
}

let private state = { Previous = Dictionary<int, Previous>(); Current = None }
let private gate = obj ()

let private processTelemetry (role: string) (previous: Previous option) (now: DateTimeOffset) (child: Process) =
    try
        child.Refresh()
        let cpuTime = child.TotalProcessorTime
        let resident = child.WorkingSet64
        let cpuPercent =
            match previous with
            | Some (previousAt, previousCpu) when now > previousAt ->
                let elapsedSeconds = (now - previousAt).TotalSeconds
                if elapsedSeconds > 0.0 then
                    ((cpuTime - previousCpu).TotalSeconds / elapsedSeconds / float Environment.ProcessorCount) * 100.0
                else
                    0.0
            | _ -> 0.0
        let item = {
            ProcessId = child.Id
            Role = role
            ResidentBytes = resident
            CpuTime = cpuTime
            CpuPercent = max 0.0 cpuPercent
        }
        Some(child.Id, (now, cpuTime), item)
    with _ ->
        None

let sample (processes: (int * string) list) : Snapshot =
    let now = DateTimeOffset.UtcNow
    let sampled = ResizeArray<int * Previous * ProcessTelemetry>()
    let nextPrevious = Dictionary<int, Previous>()
    for pid, role in processes |> List.distinctBy fst do
        try
            use child = Process.GetProcessById pid
            let previous = match state.Previous.TryGetValue pid with | true, value -> Some value | _ -> None
            match processTelemetry role previous now child with
            | Some (id, latest, item) ->
                nextPrevious[id] <- latest
                sampled.Add(id, latest, item)
            | None -> ()
        with _ -> ()
    let items = sampled |> Seq.map (fun (_, _, item) -> item) |> Seq.toList
    let snapshot = {
        SampledAt = now
        AggregateResidentBytes = items |> List.sumBy _.ResidentBytes
        AggregateCpuPercent = items |> List.sumBy _.CpuPercent
        Processes = items
    }
    lock gate (fun () ->
        state.Previous <- nextPrevious
        state.Current <- Some snapshot)
    snapshot

let current () : Snapshot option = lock gate (fun () -> state.Current)

let currentOrSample (fallbackProcesses: (int * string) list) : Snapshot =
    match current () with
    | Some snapshot -> snapshot
    | None -> sample fallbackProcesses

let reset () : unit =
    lock gate (fun () ->
        state.Previous.Clear()
        state.Current <- None)
