-- =============================================================================
-- SSE Parser Tests — sagefs/sse.lua
-- =============================================================================
-- Expert panel:
--
-- **Gillilan**: SSE is the backbone. Get the parser right — partial chunks,
--   reconnection, event types. This is the read side of CQRS.
--
-- **Seemann**: SSE parsing is a pure function: string → Event list.
--   The reconnection policy is a separate concern. Test them independently.
--
-- **Muratori**: The SSE spec is trivial. Don't over-abstract it. The parser
--   is ~20 lines. The tests should be exhaustive on the MESSAGE FORMAT, not
--   on framework ceremony.
-- =============================================================================

local sse = require("sagefs.sse")

-- ─── parse_sse_chunk: parse raw SSE text into events ─────────────────────────

describe("sse.parse_chunk", function()
  it("parses a complete event", function()
    local chunk = "event: state\ndata: {\"key\":\"value\"}\n\n"
    local events, remainder = sse.parse_chunk(chunk)
    assert.are.equal(1, #events)
    assert.are.equal("state", events[1].type)
    assert.are.equal('{"key":"value"}', events[1].data)
  end)

  it("returns remainder for incomplete event", function()
    local chunk = "event: state\ndata: {\"key\":"
    local events, remainder = sse.parse_chunk(chunk)
    assert.are.equal(0, #events)
    assert.are.equal(chunk, remainder)
  end)

  it("handles multiple events in one chunk", function()
    local chunk = "event: state\ndata: {\"a\":1}\n\nevent: state\ndata: {\"b\":2}\n\n"
    local events, remainder = sse.parse_chunk(chunk)
    assert.are.equal(2, #events)
    assert.are.equal('{"a":1}', events[1].data)
    assert.are.equal('{"b":2}', events[2].data)
  end)

  it("handles event with no data", function()
    local chunk = "event: ping\n\n"
    local events, remainder = sse.parse_chunk(chunk)
    assert.are.equal(1, #events)
    assert.are.equal("ping", events[1].type)
    assert.is_nil(events[1].data)
  end)

  it("handles data with no event type", function()
    local chunk = "data: {\"key\":\"value\"}\n\n"
    local events, remainder = sse.parse_chunk(chunk)
    assert.are.equal(1, #events)
    assert.is_nil(events[1].type)
    assert.are.equal('{"key":"value"}', events[1].data)
  end)

  it("handles multi-line data fields", function()
    local chunk = "event: state\ndata: line1\ndata: line2\n\n"
    local events, remainder = sse.parse_chunk(chunk)
    assert.are.equal(1, #events)
    assert.are.equal("line1\nline2", events[1].data)
  end)

  it("ignores comment lines (starting with :)", function()
    local chunk = ": keepalive\nevent: state\ndata: {}\n\n"
    local events, remainder = sse.parse_chunk(chunk)
    assert.are.equal(1, #events)
    assert.are.equal("state", events[1].type)
  end)

  it("handles empty chunk", function()
    local events, remainder = sse.parse_chunk("")
    assert.are.equal(0, #events)
    assert.are.equal("", remainder)
  end)

  it("accumulates across calls (streaming simulation)", function()
    -- First chunk: partial event
    local events1, rem1 = sse.parse_chunk("event: state\n")
    assert.are.equal(0, #events1)

    -- Second chunk: completes the event
    local events2, rem2 = sse.parse_chunk(rem1 .. "data: {\"x\":1}\n\n")
    assert.are.equal(1, #events2)
    assert.are.equal("state", events2[1].type)
    assert.are.equal('{"x":1}', events2[1].data)
  end)

  it("strips trailing \\r from lines (Windows CRLF)", function()
    local chunk = "event: state\r\ndata: {}\r\n\r\n"
    local events, _ = sse.parse_chunk(chunk)
    assert.are.equal(1, #events)
    assert.are.equal("state", events[1].type)
  end)
end)
