module SageFs.Tests.EventsResumeTests

// A client that loses its `/events` connection used to come back to the snapshots and nothing else:
// every frame pushed while it was away was gone. Each frame now carries an id (`<epoch>:<seq>`), and a
// client that reconnects with the last id it saw (`Last-Event-ID`) is sent what it missed first.
//
// These run the real route on a bare Kestrel host, the same harness the scope tests use.

open System.IO
open System.Net.Http
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Tests.EventsStreamScopeTests

/// Every `id:` line in what was read, in order.
let idsIn (seen: string) : string list =
  Regex.Matches(seen, @"(?m)^id: (\S+)\r?$") |> Seq.map (fun m -> m.Groups[1].Value) |> List.ofSeq

let lastIdIn (seen: string) : string =
  match List.tryLast (idsIn seen) with
  | Some id -> id
  | None -> failwithf "no id line in what was read:\n%s" seen

let parseId (wire: string) : EventId =
  match EventId.tryParse wire with
  | Ok id -> id
  | Result.Error refusal -> failwithf "'%s' is not an id: %A" wire refusal

/// Connect the way a browser's EventSource does after a drop, and read through the retry hint.
let resume (h: Harness) (query: string) (lastEventId: string) = task {
  use request = new HttpRequestMessage(HttpMethod.Get, h.BaseUrl + "/events" + query)
  request.Headers.Add("Last-Event-ID", lastEventId)
  let! resp = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
  let! body = resp.Content.ReadAsStreamAsync()
  let reader = new StreamReader(body)
  let! replay = readUntil reader "retry:"
  return reader, replay
}

[<Tests>]
let resumeTests = testList "GET /events resumes by Last-Event-ID" [

  testTask "every pushed frame is written with an id, numbered from 1 in the order it was pushed" {
    do! withServer None (fun h -> task {
      let! (reader: StreamReader), _ = open200 h ""
      h.Session.Trigger(SseFrame.daemon (wireOf markerA))
      h.Session.Trigger(SseFrame.daemon (wireOf sentinel))
      let! seen = readUntil reader sentinel
      idsIn seen |> List.map (fun wire -> (parseId wire).Seq) |> Expect.equal "ids 1 then 2" [ 1L; 2L ]
    })
  }

  testTask "WHY — a client that comes back with its last id is sent the frames it missed, and none it had" {
    do! withServer None (fun h -> task {
      let! (first: StreamReader), _ = open200 h ""
      h.Session.Trigger(SseFrame.daemon (wireOf "SEEN-1"))
      h.Session.Trigger(SseFrame.daemon (wireOf "SEEN-2"))
      let! seen = readUntil first "SEEN-2"
      let lastSeen = lastIdIn seen
      first.Dispose()
      h.Session.Trigger(SseFrame.daemon (wireOf "MISSED-1"))
      h.Session.Trigger(SseFrame.session idA (wireOf "MISSED-2"))
      let! _, caughtUp = resume h "" lastSeen
      caughtUp |> Expect.stringContains "the first missed frame" "MISSED-1"
      caughtUp |> Expect.stringContains "the second missed frame" "MISSED-2"
      Expect.isFalse "a frame the client already had was sent again" (caughtUp.Contains "SEEN-")
    })
  }

  testTask "an id from another run of the daemon is not replayed from, and the connection still gets what is pushed next" {
    do! withServer None (fun h -> task {
      h.Session.Trigger(SseFrame.daemon (wireOf "BEFORE-CONNECT"))
      let! (reader: StreamReader), caughtUp = resume h "" "some-other-run:1"
      Expect.isFalse "an id from another run was replayed from" (caughtUp.Contains "BEFORE-CONNECT")
      h.Session.Trigger(SseFrame.daemon (wireOf sentinel))
      let! seen = readUntil reader sentinel
      seen |> Expect.stringContains "what is pushed next still arrives" sentinel
    })
  }

  testTask "an id that is not an id is treated as a first connection, not refused" {
    do! withServer None (fun h -> task {
      let! (reader: StreamReader), _ = resume h "" "garbage"
      h.Session.Trigger(SseFrame.daemon (wireOf sentinel))
      let! seen = readUntil reader sentinel
      seen |> Expect.stringContains "still streams" sentinel
    })
  }

  testTask "a connection for one session is replayed that session's missed frames and the daemon's, and none of another's" {
    do! withServer None (fun h -> task {
      let! (first: StreamReader), _ = open200 h (sprintf "?sessionId=%s" idA)
      h.Session.Trigger(SseFrame.session idA (wireOf "A-SEEN"))
      let! seen = readUntil first "A-SEEN"
      let lastSeen = lastIdIn seen
      first.Dispose()
      h.Session.Trigger(SseFrame.session idB (wireOf "B-MISSED"))
      h.Session.Trigger(SseFrame.session idA (wireOf "A-MISSED"))
      let! _, caughtUp = resume h (sprintf "?sessionId=%s" idA) lastSeen
      caughtUp |> Expect.stringContains "A's missed frame" "A-MISSED"
      Expect.isFalse "B's frame was replayed to A" (caughtUp.Contains "B-MISSED")
    })
  }

  testTask "the catch-up ends with an id-only frame at the newest id, so a client that missed nothing it may see still moves up" {
    do! withServer None (fun h -> task {
      let! (first: StreamReader), _ = open200 h (sprintf "?sessionId=%s" idA)
      h.Session.Trigger(SseFrame.session idA (wireOf "A-SEEN"))
      let! seen = readUntil first "A-SEEN"
      let lastSeen = lastIdIn seen
      first.Dispose()
      h.Session.Trigger(SseFrame.session idB (wireOf "B-ONLY"))
      let! _, caughtUp = resume h (sprintf "?sessionId=%s" idA) lastSeen
      let last = parseId lastSeen
      let newest = EventId.toWire { last with Seq = last.Seq + 1L }
      idsIn caughtUp |> Expect.contains "an id-only frame at the newest id" newest
      Expect.isFalse "B's frame was replayed to A" (caughtUp.Contains "B-ONLY")
    })
  }
]
