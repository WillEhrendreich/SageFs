module SageFs.Tests.RenderInstrumentationTests

open Expecto
open Expecto.Flip
open SageFs

// ============================================================
// Render Pipeline Instrumentation Tests
// ============================================================
// Verify that render pipeline metrics are properly defined
// and can record values without exceptions.

[<Tests>]
let renderInstrumentationTests = testList "Render pipeline instrumentation" [

  testList "metric definitions" [
    test "renderScreenDrawMs histogram exists" {
      Instrumentation.renderScreenDrawMs
      |> Expect.isNotNull "histogram should be instantiated"
    }

    test "renderEmitMs histogram exists" {
      Instrumentation.renderEmitMs
      |> Expect.isNotNull "histogram should be instantiated"
    }

    test "renderConsoleWriteMs histogram exists" {
      Instrumentation.renderConsoleWriteMs
      |> Expect.isNotNull "histogram should be instantiated"
    }

    test "renderFrameTotalMs histogram exists" {
      Instrumentation.renderFrameTotalMs
      |> Expect.isNotNull "histogram should be instantiated"
    }

    test "renderDiffCellCount histogram exists" {
      Instrumentation.renderDiffCellCount
      |> Expect.isNotNull "histogram should be instantiated"
    }

    test "renderFullEmitCount counter exists" {
      Instrumentation.renderFullEmitCount
      |> Expect.isNotNull "counter should be instantiated"
    }

    test "renderDiffEmitCount counter exists" {
      Instrumentation.renderDiffEmitCount
      |> Expect.isNotNull "counter should be instantiated"
    }

    test "renderMeter exists with correct name" {
      Instrumentation.renderMeter.Name
      |> Expect.equal "meter name should match" "SageFs.RenderPipeline"
    }
  ]

  testList "metric recording" [
    test "recording to renderScreenDrawMs does not throw" {
      Instrumentation.renderScreenDrawMs.Record(1.5)
    }

    test "recording to renderEmitMs does not throw" {
      Instrumentation.renderEmitMs.Record(0.3)
    }

    test "recording to renderConsoleWriteMs does not throw" {
      Instrumentation.renderConsoleWriteMs.Record(0.1)
    }

    test "recording to renderFrameTotalMs does not throw" {
      Instrumentation.renderFrameTotalMs.Record(2.0)
    }

    test "recording to renderDiffCellCount does not throw" {
      Instrumentation.renderDiffCellCount.Record(42L)
    }

    test "incrementing renderFullEmitCount does not throw" {
      Instrumentation.renderFullEmitCount.Add(1L)
    }

    test "incrementing renderDiffEmitCount does not throw" {
      Instrumentation.renderDiffEmitCount.Add(1L)
    }
  ]
]
