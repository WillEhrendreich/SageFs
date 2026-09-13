module SageFs.Tests.DashboardAlarmTests

open System
open Expecto
open Expecto.Flip
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
      html |> Expect.stringContains "should have alarm-banner id" DomIds.AlarmBanner
    }

    test "empty list renders no alarm text" {
      let html = renderAlarmBanner [] |> render
      (html.Contains "🚨") |> Expect.isFalse "empty list should not show alarm icon"
    }

    test "single alarm renders phase" {
      let alarm = { Phase = "update"; Message = "something blew up"; Timestamp = DateTimeOffset.UtcNow }
      let html = renderAlarmBanner [ alarm ] |> render
      html |> Expect.stringContains "should show alarm phase" "update"
    }

    test "single alarm renders message" {
      let alarm = { Phase = "render"; Message = "null ref in render"; Timestamp = DateTimeOffset.UtcNow }
      let html = renderAlarmBanner [ alarm ] |> render
      html |> Expect.stringContains "should show alarm message" "null ref in render"
    }

    test "single alarm renders alarm icon" {
      let alarm = { Phase = "effect"; Message = "IO error"; Timestamp = DateTimeOffset.UtcNow }
      let html = renderAlarmBanner [ alarm ] |> render
      html |> Expect.stringContains "should render alarm icon" "🚨"
    }

    test "non-empty alarm banner renders as closed disclosure" {
      let alarm = { Phase = "effect"; Message = "IO error"; Timestamp = DateTimeOffset.UtcNow }
      let html = renderAlarmBanner [ alarm ] |> render
      html |> Expect.stringContains "alarm banner should render a disclosure wrapper" "<details"
      html |> Expect.stringContains "alarm banner should render a disclosure summary" "<summary"
      (html.Contains "<details open") |> Expect.isFalse "alarm banner should default collapsed"
    }

    test "multiple alarms all rendered" {
      let alarms = [
        { Phase = "update"; Message = "msg1"; Timestamp = DateTimeOffset.UtcNow }
        { Phase = "render"; Message = "msg2"; Timestamp = DateTimeOffset.UtcNow }
        { Phase = "callback"; Message = "msg3"; Timestamp = DateTimeOffset.UtcNow }
      ]
      let html = renderAlarmBanner alarms |> render
      html |> Expect.stringContains "should show first alarm message" "msg1"
      html |> Expect.stringContains "should show second alarm message" "msg2"
      html |> Expect.stringContains "should show third alarm message" "msg3"
    }

    test "alarm panel has dismiss button" {
      let alarm = { Phase = "update"; Message = "oops"; Timestamp = DateTimeOffset.UtcNow }
      let html = renderAlarmBanner [ alarm ] |> render
      html |> Expect.stringContains "should have dismiss button or link" "dismiss"
    }
  ]

[<Tests>]
let systemAlarmEntryTests =
  testList "SystemAlarmEntry" [

    test "creates with all fields" {
      let ts = DateTimeOffset.UtcNow
      let entry = { Phase = "update"; Message = "test error"; Timestamp = ts }
      entry.Phase |> Expect.equal "phase round-trips" "update"
      entry.Message |> Expect.equal "message round-trips" "test error"
      entry.Timestamp |> Expect.equal "timestamp round-trips" ts
    }

    test "different phases are distinct" {
      let a = { Phase = "update"; Message = "err"; Timestamp = DateTimeOffset.UtcNow }
      let b = { Phase = "render"; Message = "err"; Timestamp = DateTimeOffset.UtcNow }
      a.Phase |> Expect.notEqual "phases differ" b.Phase
    }
  ]
