module SageFs.Tests.SessionOperationsTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkerProtocol
open SageFs.SessionOperations

let private mkSession id lastActive (status: SessionStatus) : SessionInfo = {
  Id = id
  Projects = ["Test.fsproj"]
  WorkingDirectory = sprintf @"C:\%s" id
  SolutionRoot = None
  CreatedAt = DateTime(2026, 1, 1)
  LastActivity = lastActive
  Status = status
  WorkerPid = Some 100
}

let resolveSessionTests = testList "resolveSession" [
  test "no sessions returns NoActiveSessions" {
    resolveSession None []
    |> Expect.equal "should be error"
      (Result.Error SageFsError.NoActiveSessions)
  }

  test "single session, no explicit id, returns DefaultSingle" {
    let s = mkSession "s1" DateTime.UtcNow SessionStatus.Ready
    resolveSession None [s]
    |> Expect.equal "should default to single"
      (Result.Ok (SessionResolution.DefaultSingle "s1"))
  }

  test "explicit id found returns Resolved" {
    let s = mkSession "s1" DateTime.UtcNow SessionStatus.Ready
    resolveSession (Some "s1") [s]
    |> Expect.equal "should resolve"
      (Result.Ok (SessionResolution.Resolved "s1"))
  }

  test "explicit id not found returns SessionNotFound" {
    let s = mkSession "s1" DateTime.UtcNow SessionStatus.Ready
    resolveSession (Some "nope") [s]
    |> Expect.equal "should error"
      (Result.Error (SageFsError.SessionNotFound "nope"))
  }

  test "multiple sessions, no explicit id, returns most recently active" {
    let old = mkSession "old" (DateTime(2026, 1, 1)) SessionStatus.Ready
    let recent = mkSession "recent" (DateTime(2026, 2, 1)) SessionStatus.Ready
    resolveSession None [old; recent]
    |> Expect.equal "should pick most recent"
      (Result.Ok (SessionResolution.DefaultMostRecent "recent"))
  }

  test "multiple sessions, no explicit id, order-independent" {
    let old = mkSession "old" (DateTime(2026, 1, 1)) SessionStatus.Ready
    let recent = mkSession "recent" (DateTime(2026, 2, 1)) SessionStatus.Ready
    resolveSession None [recent; old]
    |> Expect.equal "should still pick most recent"
      (Result.Ok (SessionResolution.DefaultMostRecent "recent"))
  }

  test "explicit id with multiple sessions resolves correctly" {
    let s1 = mkSession "s1" (DateTime(2026, 1, 1)) SessionStatus.Ready
    let s2 = mkSession "s2" (DateTime(2026, 2, 1)) SessionStatus.Ready
    resolveSession (Some "s1") [s1; s2]
    |> Expect.equal "should resolve explicit"
      (Result.Ok (SessionResolution.Resolved "s1"))
  }
]

let sessionIdExtractionTests = testList "sessionId extraction" [
  test "extracts from Resolved" {
    sessionId (SessionResolution.Resolved "abc")
    |> Expect.equal "id" "abc"
  }
  test "extracts from DefaultSingle" {
    sessionId (SessionResolution.DefaultSingle "def")
    |> Expect.equal "id" "def"
  }
  test "extracts from DefaultMostRecent" {
    sessionId (SessionResolution.DefaultMostRecent "ghi")
    |> Expect.equal "id" "ghi"
  }
]

let describeResolutionTests = testList "describeResolution" [
  test "Resolved includes (explicit)" {
    describeResolution (SessionResolution.Resolved "abc")
    |> Expect.stringContains "has explicit" "(explicit)"
  }
  test "DefaultSingle includes (only session)" {
    describeResolution (SessionResolution.DefaultSingle "abc")
    |> Expect.stringContains "has only" "(only session)"
  }
  test "DefaultMostRecent includes (most recently active)" {
    describeResolution (SessionResolution.DefaultMostRecent "abc")
    |> Expect.stringContains "has recent" "(most recently active)"
  }
]

let describeErrorTests = testList "SageFsError.describe (session errors)" [
  test "NoActiveSessions mentions create_session" {
    SageFsError.describe SageFsError.NoActiveSessions
    |> Expect.stringContains "mentions create" "create_session"
  }
  test "SessionNotFound includes the session id" {
    SageFsError.describe (SageFsError.SessionNotFound "xyz")
    |> Expect.stringContains "mentions id" "xyz"
  }
  test "WorkerCommunicationFailed includes reason" {
    SageFsError.describe (SageFsError.WorkerCommunicationFailed("abc", "pipe broken"))
    |> Expect.stringContains "mentions reason" "pipe broken"
  }
  test "SessionCreationFailed includes reason" {
    SageFsError.describe (SageFsError.SessionCreationFailed "out of memory")
    |> Expect.stringContains "mentions reason" "out of memory"
  }
  test "AmbiguousSessions lists session descriptions" {
    SageFsError.describe (SageFsError.AmbiguousSessions ["  s1  MyApp  Ready"; "  s2  MyApp  Evaluating"])
    |> fun desc ->
      Expect.stringContains "has s1" "s1" desc
      Expect.stringContains "has s2" "s2" desc
  }
]

let private now = DateTime(2026, 2, 14, 12, 0, 0)

let private mkSessionWithPid id lastActive (status: SessionStatus) pid : SessionInfo = {
  Id = id
  Projects = ["Test.fsproj"]
  WorkingDirectory = sprintf @"C:\Code\%s" id
  SolutionRoot = None
  CreatedAt = DateTime(2026, 2, 14, 10, 0, 0)
  LastActivity = lastActive
  Status = status
  WorkerPid = pid
}

let formatSessionInfoTests = testList "formatSessionInfo" [
  test "includes all fields" {
    let s = mkSessionWithPid "abc123" (now.AddMinutes(-2.0)) SessionStatus.Ready (Some 1234)
    let output = formatSessionInfo now s
    Expect.stringContains "has id" "abc123" output
    Expect.stringContains "has state" "Ready" output
    Expect.stringContains "has PID" "PID 1234" output
    Expect.stringContains "has project" "Test.fsproj" output
    Expect.stringContains "has last active" "2 min ago" output
  }
  test "handles missing PID" {
    let s = mkSessionWithPid "abc123" now SessionStatus.Starting None
    let output = formatSessionInfo now s
    Expect.stringContains "has no PID" "(no PID)" output
  }
  test "just now for recent activity" {
    let s = mkSessionWithPid "x" (now.AddSeconds(-30.0)) SessionStatus.Ready (Some 1)
    let output = formatSessionInfo now s
    Expect.stringContains "says just now" "just now" output
  }
  test "hours ago" {
    let s = mkSessionWithPid "x" (now.AddHours(-3.0)) SessionStatus.Ready (Some 1)
    let output = formatSessionInfo now s
    Expect.stringContains "says hr ago" "3 hr ago" output
  }
  test "days ago" {
    let s = mkSessionWithPid "x" (now.AddDays(-2.0)) SessionStatus.Ready (Some 1)
    let output = formatSessionInfo now s
    Expect.stringContains "says days ago" "2 days ago" output
  }
]

let formatSessionListTests = testList "formatSessionList" [
  test "empty list" {
    formatSessionList now []
    |> Expect.equal "empty message" "No active sessions."
  }
  test "single session" {
    let s = mkSessionWithPid "s1" (now.AddMinutes(-1.0)) SessionStatus.Ready (Some 100)
    let output = formatSessionList now [s]
    Expect.stringContains "has count" "1 active session(s)" output
    Expect.stringContains "has s1" "s1" output
  }
  test "multiple sessions" {
    let s1 = mkSessionWithPid "s1" (now.AddMinutes(-1.0)) SessionStatus.Ready (Some 100)
    let s2 = mkSessionWithPid "s2" (now.AddMinutes(-5.0)) SessionStatus.Evaluating (Some 200)
    let output = formatSessionList now [s1; s2]
    Expect.stringContains "has count" "2 active session(s)" output
    Expect.stringContains "has s1" "s1" output
    Expect.stringContains "has s2" "s2" output
  }
]

[<Tests>]
let tests = testList "SessionOperations" [
  resolveSessionTests
  sessionIdExtractionTests
  describeResolutionTests
  describeErrorTests
  formatSessionInfoTests
  formatSessionListTests
]
