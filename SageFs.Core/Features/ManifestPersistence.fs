namespace SageFs.Features

open System
open System.IO
open SageFs

/// Domain types for .sagefm v1 binary format (daemon session manifest).
module ManifestTypes =

  type ManifestSessionEntry = {
    SessionId: string
    Projects: string list
    WorkingDir: string
    CreatedAt: DateTimeOffset
    StoppedAt: DateTimeOffset option
  }

  type DaemonManifestData = {
    Entries: ManifestSessionEntry list
    ActiveSessionId: string option
    CreatedAtMs: int64
  }

  module DaemonManifestData =
    let empty = {
      Entries = []
      ActiveSessionId = None
      CreatedAtMs = 0L
    }


/// Writer for .sagefm v1 binary format (daemon session manifest).
module ManifestWriter =
  open ManifestTypes

  let private writeSess (entries: ManifestSessionEntry list) : byte[] =
    use ms = new MemoryStream()
    use bw = new BinaryWriter(ms)
    bw.Write(uint32 (List.length entries))
    for e in entries do
      BinaryPrimitives.writeLpString bw e.SessionId
      bw.Write(uint32 (List.length e.Projects))
      for p in e.Projects do
        BinaryPrimitives.writeLpString bw p
      BinaryPrimitives.writeLpString bw e.WorkingDir
      bw.Write(e.CreatedAt.ToUnixTimeMilliseconds())
      match e.StoppedAt with
      | None -> bw.Write(-1L)
      | Some dto -> bw.Write(dto.ToUnixTimeMilliseconds())
    bw.Flush()
    ms.ToArray()

  let currentFormatVersion = 1us

  let write (data: DaemonManifestData) : byte[] =
    let sessPayload = writeSess data.Entries
    let sectionCount = 1u
    let headerSize = 64
    let dirEntrySize = 16
    let dirSize = int sectionCount * dirEntrySize

    let sessOffset = uint64 (headerSize + dirSize)
    let totalSize = sessOffset + uint64 sessPayload.Length

    let sessCrc = Crc32.computeAll sessPayload

    use ms = new MemoryStream()
    use bw = new BinaryWriter(ms)

    // Header (64 bytes)
    bw.Write([| 0x53uy; 0x46uy; 0x4Duy; 0x31uy |]) // "SFM1"
    bw.Write(currentFormatVersion)                    // format_version
    bw.Write(1us)                                     // min_reader_version
    bw.Write(sectionCount)                            // section_count
    bw.Write(0u)                                      // flags
    bw.Write(data.CreatedAtMs)                        // created_at_ms
    bw.Write(totalSize)                               // total_file_size
    bw.Write(uint32 (List.length data.Entries))       // session_count
    bw.Write(0u)                                      // header_crc placeholder @36
    BinaryPrimitives.writeLpStringOption bw data.ActiveSessionId
    let pos = int ms.Position
    let padLen = headerSize - pos
    match padLen > 0 with
    | true -> bw.Write(Array.zeroCreate<byte> padLen)
    | false when padLen = 0 -> ()
    | false ->
      invalidOp (sprintf "Manifest header overflow: ActiveSessionId is too long. padLen=%d. Use shorter session IDs (max ~20 chars)." padLen)

    // Directory (1 × 16 bytes: tag:u32 + offset:u64 + crc:u32)
    bw.Write(0x53455353u); bw.Write(sessOffset); bw.Write(sessCrc) // SESS

    // Payload
    bw.Write(sessPayload)
    bw.Flush()

    // Patch header CRC
    let result = ms.ToArray()
    let forCrc = Array.copy result
    forCrc.[36] <- 0uy; forCrc.[37] <- 0uy; forCrc.[38] <- 0uy; forCrc.[39] <- 0uy
    let hcrc = Crc32.computeAll forCrc
    let cb = BitConverter.GetBytes(hcrc)
    Array.Copy(cb, 0, result, 36, 4)
    result


/// Reader for .sagefm v1 binary format.
module ManifestReader =
  open ManifestTypes

  let readerVersion = 1us

  let private err msg : Result<'a, string> = Error msg

  let read (data: byte[]) : Result<DaemonManifestData, string> =
    try
      match data.Length < 64 with
      | true -> err "File too small for header"
      | false ->

      match data.[0] = 0x53uy && data.[1] = 0x46uy && data.[2] = 0x4Duy && data.[3] = 0x31uy with
      | false -> err "Invalid magic bytes (expected SFM1)"
      | true ->

      use ms = new MemoryStream(data)
      use br = new BinaryReader(ms)

      br.ReadBytes(4) |> ignore
      let formatVersion = br.ReadUInt16()

      match formatVersion with
      | 0us -> err (sprintf "Unsupported format version %d" formatVersion)
      | v when v > ManifestWriter.currentFormatVersion ->
        err (sprintf "Unsupported format version %d (expected %d)" v ManifestWriter.currentFormatVersion)
      | _ ->

      let minReaderVersion = br.ReadUInt16()
      let sectionCount = br.ReadUInt32() |> int
      let _flags = br.ReadUInt32()
      let createdAtMs = br.ReadInt64()
      let _totalSize = br.ReadUInt64()
      let _sessionCount = br.ReadUInt32()
      let storedCrc = br.ReadUInt32()

      // Verify header CRC
      let forCrc = Array.copy data
      forCrc.[36] <- 0uy; forCrc.[37] <- 0uy; forCrc.[38] <- 0uy; forCrc.[39] <- 0uy
      let computedCrc = Crc32.computeAll forCrc
      match storedCrc = computedCrc with
      | false ->
        Instrumentation.persistenceCrcErrors.Add(
          1L, System.Collections.Generic.KeyValuePair("format", box "sfm1"))
        err (sprintf "Header CRC mismatch: stored=%08x computed=%08x" storedCrc computedCrc)
      | true ->

      let activeSessionId = BinaryPrimitives.readLpStringOption br
      ms.Position <- 64L

      match minReaderVersion > readerVersion with
      | true -> err (sprintf "Format requires reader v%d but we are v%d" minReaderVersion readerVersion)
      | false ->

      let dirEntries =
        [ for _ in 0 .. sectionCount - 1 do
            let tag = br.ReadUInt32()
            let offset = br.ReadUInt64()
            let crc = br.ReadUInt32()
            (tag, offset, crc) ]

      let sessEntry = dirEntries |> List.tryFind (fun (t, _, _) -> t = 0x53455353u)
      match sessEntry with
      | None -> err "Missing SESS section"
      | Some (_, sessOffset, sessCrcStored) ->

      let payloadStart = int sessOffset
      let payloadEnd =
        dirEntries
        |> List.map (fun (_, o, _) -> int o)
        |> List.filter (fun o -> o > payloadStart)
        |> (fun offsets -> match offsets with | [] -> data.Length | xs -> List.min xs)
      let payload = data.[payloadStart .. payloadEnd - 1]

      let payloadCrc = Crc32.computeAll payload
      match payloadCrc = sessCrcStored with
      | false ->
        Instrumentation.persistenceCrcErrors.Add(
          1L, System.Collections.Generic.KeyValuePair("format", box "sfm1"))
        err (sprintf "SESS CRC mismatch: stored=%08x computed=%08x" sessCrcStored payloadCrc)
      | true ->

      use pms = new MemoryStream(payload)
      use pbr = new BinaryReader(pms)
      let count = pbr.ReadUInt32() |> int
      let entries =
        [ for _ in 0 .. count - 1 do
            let sid = BinaryPrimitives.readLpString pbr
            let projCount = pbr.ReadUInt32() |> int
            let projects = [ for _ in 0 .. projCount - 1 do BinaryPrimitives.readLpString pbr ]
            let workDir = BinaryPrimitives.readLpString pbr
            let createdMs = pbr.ReadInt64()
            let stoppedMs = pbr.ReadInt64()
            {
              SessionId = sid
              Projects = projects
              WorkingDir = workDir
              CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(createdMs)
              StoppedAt =
                match stoppedMs with
                | -1L -> None
                | v -> Some (DateTimeOffset.FromUnixTimeMilliseconds(v))
            } ]

      Ok {
        Entries = entries
        ActiveSessionId = activeSessionId
        CreatedAtMs = createdAtMs
      }

    with ex ->
      err (sprintf "Failed to parse manifest: %s" ex.Message)


/// File I/O for .sagefm daemon manifest.
module ManifestFile =
  open ManifestTypes

  let private manifestPath (sageFsDir: string) =
    Path.Combine(sageFsDir, "daemon.sagefm")

  /// Save manifest to .sagefm with atomic write.
  let save (sageFsDir: string) (data: DaemonManifestData) : Result<string, string> =
    try
      Directory.CreateDirectory(sageFsDir) |> ignore
      let path = manifestPath sageFsDir
      let tmpPath = path + ".tmp"
      let bytes = ManifestWriter.write data
      File.WriteAllBytes(tmpPath, bytes)
      File.Move(tmpPath, path, overwrite = true)
      Ok path
    with ex ->
      Error (sprintf "Failed to save manifest: %s" ex.Message)

  /// Load manifest from .sagefm.
  let load (sageFsDir: string) : Result<DaemonManifestData, string> =
    let path = manifestPath sageFsDir
    match File.Exists(path) with
    | false -> Error "No manifest file found"
    | true ->
      try
        let bytes = File.ReadAllBytes(path)
        ManifestReader.read bytes
      with ex ->
        Error (sprintf "Failed to read manifest: %s" ex.Message)


/// Mapping between DaemonReplayState and DaemonManifestData.
module ManifestMapping =
  open ManifestTypes
  open Replay

  let fromReplayState (state: DaemonReplayState) : DaemonManifestData =
    let entries =
      state.Sessions
      |> Map.toList
      |> List.map (fun (_, r: DaemonSessionRecord) ->
        {
          ManifestSessionEntry.SessionId = r.SessionId
          Projects = r.Projects
          WorkingDir = r.WorkingDir
          CreatedAt = r.CreatedAt
          StoppedAt = r.StoppedAt
        })
    {
      Entries = entries
      ActiveSessionId = state.ActiveSessionId
      CreatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    }

  let toReplayState (manifest: DaemonManifestData) : DaemonReplayState =
    let cutoff = DateTimeOffset.UtcNow.AddDays(-7.0)
    let sessions =
      manifest.Entries
      |> List.filter (fun (e: ManifestSessionEntry) ->
        // Prune entries older than 7 days that are already stopped
        not (e.StoppedAt.IsSome && e.CreatedAt < cutoff))
      |> List.map (fun (e: ManifestSessionEntry) ->
        e.SessionId,
        {
          DaemonSessionRecord.SessionId = e.SessionId
          Projects = e.Projects
          WorkingDir = e.WorkingDir
          CreatedAt = e.CreatedAt
          StoppedAt = e.StoppedAt
        })
      |> Map.ofList
    {
      Sessions = sessions
      ActiveSessionId = manifest.ActiveSessionId
    }
