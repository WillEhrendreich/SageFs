-- =============================================================================
-- Model Tests — sagefs/model.lua
-- =============================================================================
--
--   the UI is just a projection. Cell states are: idle → running → success|error.
--   Code changes push success/error → stale.
--
--   invalid transitions impossible (can't go from idle to stale directly).
--
--   Keep it pure. No vim APIs in the model.
-- =============================================================================

local model = require("sagefs.model")

-- ─── new: create fresh model ─────────────────────────────────────────────────

describe("model.new", function()
  it("creates model with empty cells", function()
    local m = model.new()
    assert.is_not_nil(m)
    assert.is_not_nil(m.cells)
    assert.are.equal(0, model.cell_count(m))
  end)

  it("creates model with disconnected status", function()
    local m = model.new()
    assert.are.equal("disconnected", m.status)
  end)
end)

-- ─── set_cell_state: track cell evaluation state ─────────────────────────────

describe("model.set_cell_state", function()
  it("sets cell to running", function()
    local m = model.new()
    m = model.set_cell_state(m, 1, "running")
    assert.are.equal("running", model.get_cell_state(m, 1).status)
  end)

  it("sets cell to success with output", function()
    local m = model.new()
    m = model.set_cell_state(m, 1, "success", "val it: int = 42")
    local cell = model.get_cell_state(m, 1)
    assert.are.equal("success", cell.status)
    assert.are.equal("val it: int = 42", cell.output)
  end)

  it("sets cell to error with message", function()
    local m = model.new()
    m = model.set_cell_state(m, 1, "error", "type mismatch")
    local cell = model.get_cell_state(m, 1)
    assert.are.equal("error", cell.status)
    assert.are.equal("type mismatch", cell.output)
  end)

  it("overwrites previous state", function()
    local m = model.new()
    m = model.set_cell_state(m, 1, "running")
    m = model.set_cell_state(m, 1, "success", "done")
    assert.are.equal("success", model.get_cell_state(m, 1).status)
  end)

  it("tracks multiple cells independently", function()
    local m = model.new()
    m = model.set_cell_state(m, 1, "success", "42")
    m = model.set_cell_state(m, 2, "error", "oops")
    assert.are.equal("success", model.get_cell_state(m, 1).status)
    assert.are.equal("error", model.get_cell_state(m, 2).status)
  end)
end)

-- ─── get_cell_state: query cell state ────────────────────────────────────────

describe("model.get_cell_state", function()
  it("returns idle for unknown cell", function()
    local m = model.new()
    local cell = model.get_cell_state(m, 99)
    assert.are.equal("idle", cell.status)
    assert.is_nil(cell.output)
  end)
end)

-- ─── mark_stale: mark cells as stale when code changes ──────────────────────

describe("model.mark_stale", function()
  it("marks a successful cell as stale", function()
    local m = model.new()
    m = model.set_cell_state(m, 1, "success", "42")
    m = model.mark_stale(m, 1)
    assert.are.equal("stale", model.get_cell_state(m, 1).status)
  end)

  it("marks an error cell as stale", function()
    local m = model.new()
    m = model.set_cell_state(m, 1, "error", "oops")
    m = model.mark_stale(m, 1)
    assert.are.equal("stale", model.get_cell_state(m, 1).status)
  end)

  it("preserves output when marking stale", function()
    local m = model.new()
    m = model.set_cell_state(m, 1, "success", "42")
    m = model.mark_stale(m, 1)
    assert.are.equal("42", model.get_cell_state(m, 1).output)
  end)

  it("is a no-op for idle cells", function()
    local m = model.new()
    m = model.mark_stale(m, 1)
    assert.are.equal("idle", model.get_cell_state(m, 1).status)
  end)

  it("is a no-op for running cells", function()
    local m = model.new()
    m = model.set_cell_state(m, 1, "running")
    m = model.mark_stale(m, 1)
    assert.are.equal("running", model.get_cell_state(m, 1).status)
  end)
end)

-- ─── mark_all_stale: mark all evaluated cells as stale ───────────────────────

describe("model.mark_all_stale", function()
  it("marks all success/error cells as stale", function()
    local m = model.new()
    m = model.set_cell_state(m, 1, "success", "42")
    m = model.set_cell_state(m, 2, "error", "oops")
    m = model.set_cell_state(m, 3, "running")
    m = model.mark_all_stale(m)
    assert.are.equal("stale", model.get_cell_state(m, 1).status)
    assert.are.equal("stale", model.get_cell_state(m, 2).status)
    assert.are.equal("running", model.get_cell_state(m, 3).status)
  end)
end)

-- ─── clear_cells: reset all cell state ───────────────────────────────────────

describe("model.clear_cells", function()
  it("removes all cell state", function()
    local m = model.new()
    m = model.set_cell_state(m, 1, "success", "42")
    m = model.set_cell_state(m, 2, "error", "oops")
    m = model.clear_cells(m)
    assert.are.equal(0, model.cell_count(m))
    assert.are.equal("idle", model.get_cell_state(m, 1).status)
  end)
end)

-- ─── set_status: connection status ───────────────────────────────────────────

describe("model.set_status", function()
  it("sets connected status", function()
    local m = model.new()
    m = model.set_status(m, "connected")
    assert.are.equal("connected", m.status)
  end)

  it("sets disconnected status", function()
    local m = model.new()
    m = model.set_status(m, "connected")
    m = model.set_status(m, "disconnected")
    assert.are.equal("disconnected", m.status)
  end)
end)
