module SageFs.Tests.DashboardAlarmTests

open System
open Expecto
open Falco.Markup
open SageFs.Server
open SageFs.Server.DashboardTypes
open SageFs.Server.DashboardFragments

let private render (node: XmlNode) = renderNode node

[<Tests>]
let alarmBannerRenderTests =
  testList "renderAlarmBanner HTML" [

    test "empty list renders empty alarm panel with id" {
      let html = renderAlarmBanner [] |> render
      Expect.stringContains html DomIds.AlarmBanner "should have alarm-banner id"
    }

    test "empty list renders no alarm text" {
      let html = renderAlarmBanner [] |> render
      Expect.isFalse (html.Contains "🚨") "empty list should not show alarm icon"
    }

    test "single alarm renders phase" {
      let alarm = { Phase = "update"; Message = "something blew up"; Timestamp = DateTimeOffset.UtcNow }
      let html = renderAlarmBanner [ alarm ] |> render
      Expect.stringContains html "update" "should show alarm phase"
    }

    test "single alarm renders message" {
      let alarm = { Phase = "render"; Message = "null ref in render"; Timestamp = DateTimeOffset.UtcNow }
      let html = renderAlarmBanner [ alarm ] |> render
      Expect.stringContains html "null ref in render" "should show alarm message"
    }

    test "single alarm renders alarm icon" {
      let alarm = { Phase = "effect"; Message = "IO error"; Timestamp = DateTimeOffset.UtcNow }
      let html = renderAlarmBanner [ alarm ] |> render
      Expect.stringContains html "🚨" "should render alarm icon"
    }

    test "multiple alarms all rendered" {
      let alarms = [
        { Phase = "update"; Message = "msg1"; Timestamp = DateTimeOffset.UtcNow }
        { Phase = "render"; Message = "msg2"; Timestamp = DateTimeOffset.UtcNow }
        { Phase = "callback"; Message = "msg3"; Timestamp = DateTimeOffset.UtcNow }
      ]
      let html = renderAlarmBanner alarms |> render
      Expect.stringContains html "msg1" "should show first alarm message"
      Expect.stringContains html "msg2" "should show second alarm message"
      Expect.stringContains html "msg3" "should show third alarm message"
    }

    test "alarm panel has dismiss button" {
      let alarm = { Phase = "update"; Message = "oops"; Timestamp = DateTimeOffset.UtcNow }
      let html = renderAlarmBanner [ alarm ] |> render
      Expect.stringContains html "dismiss" "should have dismiss button or link"
    }
  ]

[<Tests>]
let systemAlarmEntryTests =
  testList "SystemAlarmEntry" [

    test "creates with all fields" {
      let ts = DateTimeOffset.UtcNow
      let entry = { Phase = "update"; Message = "test error"; Timestamp = ts }
      Expect.equal entry.Phase "update" "phase round-trips"
      Expect.equal entry.Message "test error" "message round-trips"
      Expect.equal entry.Timestamp ts "timestamp round-trips"
    }

    test "different phases are distinct" {
      let a = { Phase = "update"; Message = "err"; Timestamp = DateTimeOffset.UtcNow }
      let b = { Phase = "render"; Message = "err"; Timestamp = DateTimeOffset.UtcNow }
      Expect.notEqual a.Phase b.Phase "phases differ"
    }
  ]
