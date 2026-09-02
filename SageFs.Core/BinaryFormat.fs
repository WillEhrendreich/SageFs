namespace SageFs

open System
open System.IO
open System.Text

/// CRC-32 (ISO 3309, polynomial 0xEDB88320).
module Crc32 =

  let private table =
    [| for i in 0u .. 255u do
        let mutable crc = i
        for _ in 0 .. 7 do
          crc <-
            match crc &&& 1u with
            | 1u -> (crc >>> 1) ^^^ 0xEDB88320u
            | _ -> crc >>> 1
        yield crc |]

  let compute (data: byte[]) (offset: int) (length: int) : uint32 =
    let mutable crc = 0xFFFFFFFFu
    for i in offset .. offset + length - 1 do
      let idx = (crc ^^^ uint32 data.[i]) &&& 0xFFu |> int
      crc <- (crc >>> 8) ^^^ table.[idx]
    crc ^^^ 0xFFFFFFFFu

  let computeAll (data: byte[]) : uint32 =
    compute data 0 data.Length


/// Binary read/write primitives for .sagefs and .sagetc formats.
module BinaryPrimitives =

  /// Write length-prefixed UTF-8 string: u32 length + bytes.
  let writeLpString (bw: BinaryWriter) (s: string) =
    let bytes = Encoding.UTF8.GetBytes(s)
    bw.Write(uint32 bytes.Length)
    bw.Write(bytes)

  /// Read length-prefixed UTF-8 string with bounds validation against the
  /// remaining bytes in the stream, capped by an explicit caller-supplied
  /// budget. Section parsers pass the section's remaining byte count so the
  /// check is against the SECTION SLICE, never the whole stream — a reader that
  /// operates on a window of a larger buffer must not be able to read past the
  /// slice it was given.
  let readLpStringBounded (br: BinaryReader) (maxRemaining: int64) : string =
    let lenRaw = br.ReadUInt32()
    match lenRaw > uint32 System.Int32.MaxValue with
    | true ->
      invalidOp (sprintf "lp-string length %u exceeds maximum safe string length (Int32.MaxValue)" lenRaw)
    | false ->
    let len = int lenRaw
    let streamRemaining = br.BaseStream.Length - br.BaseStream.Position
    let remaining = min maxRemaining streamRemaining
    match int64 len > remaining with
    | true ->
      invalidOp (sprintf "lp-string length %d exceeds remaining %d bytes" len remaining)
    | false ->
      let bytes = br.ReadBytes(len)
      Encoding.UTF8.GetString(bytes)

  /// Read length-prefixed UTF-8 string with bounds validation.
  /// Guards against uint32→int32 overflow (lengths > 2GB) and truncated streams.
  let readLpString (br: BinaryReader) : string =
    readLpStringBounded br System.Int64.MaxValue

  /// Write optional lp-string: 0xFFFFFFFF = None, else lp-string.
  let writeLpStringOption (bw: BinaryWriter) (opt: string option) =
    match opt with
    | None -> bw.Write(0xFFFFFFFFu)
    | Some s -> writeLpString bw s

  /// Read optional lp-string with bounds validation against the remaining bytes
  /// in the stream, capped by an explicit caller-supplied budget (see
  /// readLpStringBounded — section parsers must bound by the section slice).
  let readLpStringOptionBounded (br: BinaryReader) (maxRemaining: int64) : string option =
    let marker = br.ReadUInt32()
    match marker with
    | 0xFFFFFFFFu -> None
    | len ->
      match len > uint32 System.Int32.MaxValue with
      | true ->
        invalidOp (sprintf "lp-string-option length %d overflows Int32 (corrupt data)" len)
      | false ->
      let streamRemaining = br.BaseStream.Length - br.BaseStream.Position
      let remaining = min maxRemaining streamRemaining
      match int64 len > remaining with
      | true ->
        invalidOp (sprintf "lp-string-option length %d exceeds remaining %d bytes" (int len) remaining)
      | false ->
        let bytes = br.ReadBytes(int len)
        Some(Encoding.UTF8.GetString(bytes))

  /// Read optional lp-string with bounds validation.
  let readLpStringOption (br: BinaryReader) : string option =
    readLpStringOptionBounded br System.Int64.MaxValue

  let writeU8 (bw: BinaryWriter) (v: byte) = bw.Write(v)
  let writeU16 (bw: BinaryWriter) (v: uint16) = bw.Write(v)
  let writeU32 (bw: BinaryWriter) (v: uint32) = bw.Write(v)
  let writeU64 (bw: BinaryWriter) (v: uint64) = bw.Write(v)
  let writeI64 (bw: BinaryWriter) (v: int64) = bw.Write(v)
