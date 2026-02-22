-- =============================================================================
-- Result Formatting Tests — sagefs/format.lua
-- =============================================================================
-- Architecture test plan:
--
--   In Lua we model this as {ok=true, output=...} | {ok=false, error=...}.
--   Every display function must handle both cases.
--
--   always scroll to the SageFs window for the full thing.
--
--   width. Format for THAT display, not for a terminal.
-- =============================================================================

local format = require("sagefs.format")

-- ─── parse_exec_response: parse JSON from /exec endpoint ─────────────────────

describe("format.parse_exec_response", function()
  it("parses successful response", function()
    local json = '{"success":true,"result":"val it: int = 42"}'
    local result = format.parse_exec_response(json)
    assert.is_true(result.ok)
    assert.are.equal("val it: int = 42", result.output)
  end)

  it("parses error response", function()
    local json = '{"success":false,"result":"error FS0001: The type int does not match the type string"}'
    local result = format.parse_exec_response(json)
    assert.is_false(result.ok)
    assert.are.equal("error FS0001: The type int does not match the type string", result.error)
  end)

  it("handles malformed JSON gracefully", function()
    local result = format.parse_exec_response("not json at all")
    assert.is_false(result.ok)
    assert.is_not_nil(result.error)
  end)

  it("handles empty string", function()
    local result = format.parse_exec_response("")
    assert.is_false(result.ok)
  end)

  it("handles nil input", function()
    local result = format.parse_exec_response(nil)
    assert.is_false(result.ok)
  end)

  it("handles response with missing fields", function()
    local json = '{"success":true}'
    local result = format.parse_exec_response(json)
    assert.is_true(result.ok)
    assert.are.equal("", result.output)
  end)
end)

-- ─── format_inline: format result for extmark virtual text ───────────────────

describe("format.format_inline", function()
  it("formats success with short output", function()
    local result = format.format_inline({ ok = true, output = "val it: int = 42" })
    assert.is_not_nil(result)
    assert.is_not_nil(result.text)
    assert.is_not_nil(result.hl)
    assert.is_true(result.text:find("42") ~= nil)
  end)

  it("formats error with message", function()
    local result = format.format_inline({ ok = false, error = "type mismatch" })
    assert.is_not_nil(result)
    assert.is_true(result.text:find("type mismatch") ~= nil)
  end)

  it("truncates long output", function()
    local long_output = string.rep("x", 500)
    local result = format.format_inline({ ok = true, output = long_output })
    assert.is_true(#result.text < 200)
    assert.is_true(result.text:find("…") ~= nil or result.text:find("%.%.%.") ~= nil)
  end)

  it("uses success highlight for ok results", function()
    local result = format.format_inline({ ok = true, output = "val it: int = 42" })
    assert.are.equal("SageFsSuccess", result.hl)
  end)

  it("uses error highlight for failed results", function()
    local result = format.format_inline({ ok = false, error = "error" })
    assert.are.equal("SageFsError", result.hl)
  end)

  it("handles multiline output by taking first line", function()
    local result = format.format_inline({
      ok = true,
      output = "val it: int = 42\nval it2: string = \"hello\"",
    })
    -- Should show first line, indicate more lines exist
    assert.is_not_nil(result.text)
  end)
end)

-- ─── format_virtual_lines: format result for virtual lines below ;; ──────────

describe("format.format_virtual_lines", function()
  it("returns lines for single-line output", function()
    local result = format.format_virtual_lines({ ok = true, output = "val it: int = 42" })
    assert.are.equal(1, #result)
    assert.is_true(result[1].text:find("42") ~= nil)
  end)

  it("returns multiple lines for multiline output", function()
    local output = "val x: int = 1\nval y: int = 2\nval z: int = 3"
    local result = format.format_virtual_lines({ ok = true, output = output })
    assert.are.equal(3, #result)
  end)

  it("caps maximum lines", function()
    local lines = {}
    for i = 1, 50 do
      lines[i] = "val v" .. i .. ": int = " .. i
    end
    local output = table.concat(lines, "\n")
    local result = format.format_virtual_lines({ ok = true, output = output })
    assert.is_true(#result <= 20)
  end)

  it("uses correct highlight groups", function()
    local result = format.format_virtual_lines({ ok = true, output = "val it: int = 42" })
    assert.are.equal("SageFsOutput", result[1].hl)

    local err_result = format.format_virtual_lines({ ok = false, error = "error FS0001" })
    assert.are.equal("SageFsError", err_result[1].hl)
  end)

  it("handles empty output", function()
    local result = format.format_virtual_lines({ ok = true, output = "" })
    assert.are.equal(1, #result)
    -- Should show something like "(no output)"
  end)
end)

-- ─── format_gutter_sign: format the gutter indicator ─────────────────────────

describe("format.gutter_sign", function()
  it("returns checkmark for success", function()
    local result = format.gutter_sign("success")
    assert.are.equal("✓", result.text)
    assert.are.equal("SageFsSuccess", result.hl)
  end)

  it("returns X for error", function()
    local result = format.gutter_sign("error")
    assert.are.equal("✖", result.text)
    assert.are.equal("SageFsError", result.hl)
  end)

  it("returns spinner for running", function()
    local result = format.gutter_sign("running")
    assert.are.equal("⏳", result.text)
    assert.are.equal("SageFsRunning", result.hl)
  end)

  it("returns tilde for stale", function()
    local result = format.gutter_sign("stale")
    assert.are.equal("~", result.text)
    assert.are.equal("SageFsStale", result.hl)
  end)

  it("returns empty for idle", function()
    local result = format.gutter_sign("idle")
    assert.are.equal(" ", result.text)
  end)
end)
