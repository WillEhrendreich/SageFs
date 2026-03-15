# 📡 SSE Events Reference

All connected editors receive these events via the SSE stream. Events are tagged with `SessionId` for multi-session isolation.

| Event | Description |
|:---|:---|
| `test_source_locations` | Maps test names to file paths and line ranges for source navigation. |
| `file_annotations` | Per-file coverage health (AllPassing/SomeFailing/NoCoverage) and inline failure details. |
| `failure_narratives` | Enriched test failure context with causal analysis — which symbols/files changed, time since last pass. |
