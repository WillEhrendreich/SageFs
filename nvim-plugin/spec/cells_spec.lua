-- =============================================================================
-- Cell Parsing Tests — sagefs/cells.lua
-- =============================================================================
-- Expert panel test plan:
--
-- **DeVries**: Cells are the atomic unit. Get this right and everything else
--   flows. Test every boundary condition — off-by-one errors here will haunt
--   every feature that depends on "which cell am I in?"
--
-- **Wlaschin**: Think in terms of the DOMAIN. A cell is a region of F# code
--   delimited by `;;`. The function `find_cell(lines, cursor_line)` returns
--   {start_line, end_line, text} or nil. Make illegal states unrepresentable.
--
-- **Muratori**: Don't overthink it. Buffer is an array of strings. `;;` at end
--   of a line = cell boundary. Scan forward and backward from cursor. That's it.
--   The edge cases ARE the tests.
--
-- **Seemann**: The cell finder is a pure function: (string[], int) -> Cell | nil.
--   No side effects, no vim APIs. This is exactly what property-based testing
--   is for, but start with examples.
--
-- **Primeagen**: Test the hot path. `find_cell` gets called on every Alt-Enter.
--   If it's slow on a 2000-line file, you'll feel it. Include a perf test.
-- =============================================================================

local cells = require("sagefs.cells")

-- ─── find_boundaries: locate all ;; positions in a buffer ────────────────────

describe("cells.find_boundaries", function()
  it("finds no boundaries in empty buffer", function()
    local result = cells.find_boundaries({})
    assert.are.same({}, result)
  end)

  it("finds no boundaries in buffer without ;;", function()
    local lines = { "let x = 1", "let y = 2" }
    local result = cells.find_boundaries(lines)
    assert.are.same({}, result)
  end)

  it("finds ;; at end of line", function()
    local lines = { "let x = 1;;", "let y = 2" }
    local result = cells.find_boundaries(lines)
    assert.are.same({ 1 }, result)
  end)

  it("finds ;; with trailing whitespace", function()
    local lines = { "let x = 1;;  ", "let y = 2" }
    local result = cells.find_boundaries(lines)
    assert.are.same({ 1 }, result)
  end)

  it("finds standalone ;; on its own line", function()
    local lines = { "let x = 1", ";;" }
    local result = cells.find_boundaries(lines)
    assert.are.same({ 2 }, result)
  end)

  it("finds multiple boundaries", function()
    local lines = {
      "let x = 1;;",
      "let y = 2;;",
      "let z = 3",
    }
    local result = cells.find_boundaries(lines)
    assert.are.same({ 1, 2 }, result)
  end)

  it("finds ;; in multiline cell", function()
    local lines = {
      "let add x y =",
      "  x + y;;",
      "let z = 3",
    }
    local result = cells.find_boundaries(lines)
    assert.are.same({ 2 }, result)
  end)

  it("ignores ;; inside string literals", function()
    -- ;; inside a quoted string should NOT be a cell boundary
    local lines = { 'let s = "hello;;"' }
    local result = cells.find_boundaries(lines)
    assert.are.same({}, result)
  end)

  it("ignores ;; inside F# triple-quoted strings", function()
    local lines = { 'let s = """hello;;"""' }
    local result = cells.find_boundaries(lines)
    assert.are.same({}, result)
  end)

  it("ignores ;; inside line comments", function()
    local lines = { "// let x = 1;;" }
    local result = cells.find_boundaries(lines)
    assert.are.same({}, result)
  end)

  it("finds ;; after code even with comment on previous line", function()
    local lines = { "// comment", "let x = 1;;" }
    local result = cells.find_boundaries(lines)
    assert.are.same({ 2 }, result)
  end)
end)

-- ─── find_cell: get the cell containing cursor position ──────────────────────

describe("cells.find_cell", function()
  it("returns nil for empty buffer", function()
    local result = cells.find_cell({}, 1)
    assert.is_nil(result)
  end)

  it("returns entire buffer as one cell when no ;; present", function()
    local lines = { "let x = 1", "let y = 2" }
    local result = cells.find_cell(lines, 1)
    assert.is_not_nil(result)
    assert.are.equal(1, result.start_line)
    assert.are.equal(2, result.end_line)
    assert.are.equal("let x = 1\nlet y = 2", result.text)
  end)

  it("returns first cell when cursor is before first ;;", function()
    local lines = { "let x = 1", "let y = 2;;", "let z = 3" }
    local result = cells.find_cell(lines, 1)
    assert.is_not_nil(result)
    assert.are.equal(1, result.start_line)
    assert.are.equal(2, result.end_line)
    assert.are.equal("let x = 1\nlet y = 2;;", result.text)
  end)

  it("returns first cell when cursor is ON the ;; line", function()
    local lines = { "let x = 1", "let y = 2;;", "let z = 3" }
    local result = cells.find_cell(lines, 2)
    assert.is_not_nil(result)
    assert.are.equal(1, result.start_line)
    assert.are.equal(2, result.end_line)
  end)

  it("returns second cell when cursor is after first ;;", function()
    local lines = { "let x = 1;;", "let y = 2;;", "let z = 3" }
    local result = cells.find_cell(lines, 2)
    assert.is_not_nil(result)
    assert.are.equal(2, result.start_line)
    assert.are.equal(2, result.end_line)
    assert.are.equal("let y = 2;;", result.text)
  end)

  it("returns last cell (unterminated) when cursor is after last ;;", function()
    local lines = { "let x = 1;;", "let y = 2", "let z = 3" }
    local result = cells.find_cell(lines, 3)
    assert.is_not_nil(result)
    assert.are.equal(2, result.start_line)
    assert.are.equal(3, result.end_line)
    assert.are.equal("let y = 2\nlet z = 3", result.text)
  end)

  it("handles single-line cell", function()
    local lines = { "1 + 1;;" }
    local result = cells.find_cell(lines, 1)
    assert.is_not_nil(result)
    assert.are.equal(1, result.start_line)
    assert.are.equal(1, result.end_line)
    assert.are.equal("1 + 1;;", result.text)
  end)

  it("handles multiline cell with type definition", function()
    local lines = {
      "type Person = {",
      "  Name: string",
      "  Age: int",
      "};;",
      "let p = { Name = \"Alice\"; Age = 30 };;",
    }
    local result = cells.find_cell(lines, 2)
    assert.is_not_nil(result)
    assert.are.equal(1, result.start_line)
    assert.are.equal(4, result.end_line)
    assert.are.equal(
      "type Person = {\n  Name: string\n  Age: int\n};;",
      result.text
    )
  end)

  it("skips empty lines between cells", function()
    local lines = { "let x = 1;;", "", "let y = 2;;" }
    local result = cells.find_cell(lines, 3)
    assert.is_not_nil(result)
    assert.are.equal(2, result.start_line)
    assert.are.equal(3, result.end_line)
  end)

  it("returns nil for cursor line out of range", function()
    local lines = { "let x = 1;;" }
    assert.is_nil(cells.find_cell(lines, 0))
    assert.is_nil(cells.find_cell(lines, 5))
  end)

  it("handles cursor on empty line within a cell", function()
    local lines = { "let x = 1", "", "let y = 2;;" }
    local result = cells.find_cell(lines, 2)
    assert.is_not_nil(result)
    assert.are.equal(1, result.start_line)
    assert.are.equal(3, result.end_line)
  end)
end)

-- ─── find_all_cells: enumerate every cell in the buffer ──────────────────────

describe("cells.find_all_cells", function()
  it("returns empty list for empty buffer", function()
    assert.are.same({}, cells.find_all_cells({}))
  end)

  it("returns single cell for buffer with no ;;", function()
    local lines = { "let x = 1" }
    local result = cells.find_all_cells(lines)
    assert.are.equal(1, #result)
    assert.are.equal(1, result[1].start_line)
    assert.are.equal(1, result[1].end_line)
  end)

  it("returns two cells split by ;;", function()
    local lines = { "let x = 1;;", "let y = 2;;" }
    local result = cells.find_all_cells(lines)
    assert.are.equal(2, #result)
    assert.are.equal(1, result[1].start_line)
    assert.are.equal(1, result[1].end_line)
    assert.are.equal(2, result[2].start_line)
    assert.are.equal(2, result[2].end_line)
  end)

  it("includes trailing unterminated cell", function()
    local lines = { "let x = 1;;", "let y = 2" }
    local result = cells.find_all_cells(lines)
    assert.are.equal(2, #result)
    assert.are.equal(2, result[2].start_line)
    assert.are.equal(2, result[2].end_line)
  end)

  it("handles complex multi-cell buffer", function()
    local lines = {
      "// Cell 1",
      "let x = 1;;",
      "",
      "// Cell 2",
      "let add a b =",
      "  a + b;;",
      "",
      "// Cell 3 (unterminated)",
      "let z = add 1 2",
    }
    local result = cells.find_all_cells(lines)
    assert.are.equal(3, #result)
    assert.are.equal(1, result[1].start_line)
    assert.are.equal(2, result[1].end_line)
    assert.are.equal(3, result[2].start_line)
    assert.are.equal(6, result[2].end_line)
    assert.are.equal(7, result[3].start_line)
    assert.are.equal(9, result[3].end_line)
  end)

  it("assigns sequential cell IDs starting from 1", function()
    local lines = { "let x = 1;;", "let y = 2;;", "let z = 3;;" }
    local result = cells.find_all_cells(lines)
    assert.are.equal(1, result[1].id)
    assert.are.equal(2, result[2].id)
    assert.are.equal(3, result[3].id)
  end)
end)

-- ─── prepare_code: normalize code for submission ─────────────────────────────

describe("cells.prepare_code", function()
  it("appends ;; if missing", function()
    assert.are.equal("let x = 1;;", cells.prepare_code("let x = 1"))
  end)

  it("does not double-append ;;", function()
    assert.are.equal("let x = 1;;", cells.prepare_code("let x = 1;;"))
  end)

  it("handles trailing whitespace before ;;", function()
    assert.are.equal("let x = 1;;", cells.prepare_code("let x = 1;;  "))
  end)

  it("trims trailing whitespace", function()
    assert.are.equal("let x = 1;;", cells.prepare_code("let x = 1  "))
  end)

  it("trims leading blank lines", function()
    assert.are.equal("let x = 1;;", cells.prepare_code("\n\nlet x = 1"))
  end)

  it("trims trailing blank lines", function()
    assert.are.equal("let x = 1;;", cells.prepare_code("let x = 1\n\n"))
  end)

  it("preserves internal blank lines", function()
    local input = "let x = 1\n\nlet y = 2"
    local result = cells.prepare_code(input)
    assert.are.equal("let x = 1\n\nlet y = 2;;", result)
  end)

  it("returns nil for empty input", function()
    assert.is_nil(cells.prepare_code(""))
  end)

  it("returns nil for whitespace-only input", function()
    assert.is_nil(cells.prepare_code("   \n  \n  "))
  end)
end)

-- ─── is_boundary_line: check if a line ends a cell ───────────────────────────

describe("cells.is_boundary_line", function()
  it("recognizes ;; at end of line", function()
    assert.is_true(cells.is_boundary_line("let x = 1;;"))
  end)

  it("recognizes ;; with trailing spaces", function()
    assert.is_true(cells.is_boundary_line("let x = 1;;   "))
  end)

  it("recognizes standalone ;;", function()
    assert.is_true(cells.is_boundary_line(";;"))
  end)

  it("recognizes ;; with leading spaces", function()
    assert.is_true(cells.is_boundary_line("  ;;"))
  end)

  it("rejects ;; in middle of expression", function()
    assert.is_false(cells.is_boundary_line("let x = 1;; let y = 2"))
  end)

  it("rejects lines without ;;", function()
    assert.is_false(cells.is_boundary_line("let x = 1"))
  end)

  it("rejects ;; inside string", function()
    assert.is_false(cells.is_boundary_line('let s = "hello;;"'))
  end)

  it("rejects ;; inside comment", function()
    assert.is_false(cells.is_boundary_line("// hello;;"))
  end)

  it("rejects empty line", function()
    assert.is_false(cells.is_boundary_line(""))
  end)
end)
