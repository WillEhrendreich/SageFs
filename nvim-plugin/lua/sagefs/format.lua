-- sagefs/format.lua — Result formatting for extmarks and virtual text
-- Pure Lua, no vim API dependencies — fully testable with busted
local M = {}

local MAX_INLINE_LEN = 120
local MAX_VIRTUAL_LINES = 20

-- Simple JSON decoder for busted tests (outside Neovim)
local function json_decode(s)
  -- Try vim.json first (available in Neovim), fall back to vim.fn
  if vim and vim.json and vim.json.decode then
    return pcall(vim.json.decode, s)
  elseif vim and vim.fn and vim.fn.json_decode then
    return pcall(vim.fn.json_decode, s)
  end
  return false, "no JSON decoder available"
end

--- Parse the JSON response from POST /exec
---@param json_str string|nil
---@return {ok: boolean, output: string?, error: string?}
function M.parse_exec_response(json_str)
  if not json_str or json_str == "" then
    return { ok = false, error = "empty response" }
  end

  local ok, data = json_decode(json_str)
  if not ok or type(data) ~= "table" then
    return { ok = false, error = "invalid JSON: " .. tostring(json_str) }
  end

  if data.success then
    return { ok = true, output = data.result or "" }
  else
    return { ok = false, error = data.result or data.error or "unknown error" }
  end
end

--- Format result for inline extmark (single line, truncated)
---@param result {ok: boolean, output: string?, error: string?}
---@return {text: string, hl: string}
function M.format_inline(result)
  local text, hl

  if result.ok then
    hl = "SageFsSuccess"
    text = result.output or ""
    -- Take first line only for inline display
    local first_line = text:match("^([^\n]*)")
    if first_line then text = first_line end
    -- Indicate more lines exist
    if (result.output or ""):find("\n") then
      text = text .. " …"
    end
  else
    hl = "SageFsError"
    text = result.error or "error"
    local first_line = text:match("^([^\n]*)")
    if first_line then text = first_line end
  end

  -- Truncate long output
  if #text > MAX_INLINE_LEN then
    text = text:sub(1, MAX_INLINE_LEN - 1) .. "…"
  end

  -- Prefix with status indicator
  local prefix = result.ok and "→ " or "✖ "
  text = prefix .. text

  return { text = text, hl = hl }
end

--- Format result for virtual lines display (below ;;)
---@param result {ok: boolean, output: string?, error: string?}
---@return {text: string, hl: string}[]
function M.format_virtual_lines(result)
  local hl = result.ok and "SageFsOutput" or "SageFsError"
  local raw = result.ok and (result.output or "") or (result.error or "error")

  if raw == "" then
    return { { text = "(no output)", hl = hl } }
  end

  local lines = {}
  for line in (raw .. "\n"):gmatch("([^\n]*)\n") do
    table.insert(lines, { text = "  " .. line, hl = hl })
    if #lines >= MAX_VIRTUAL_LINES - 1 then
      table.insert(lines, { text = "  … (truncated)", hl = hl })
      break
    end
  end

  return lines
end

--- Get gutter sign for a cell status
---@param status string "success"|"error"|"running"|"stale"|"idle"
---@return {text: string, hl: string}
function M.gutter_sign(status)
  if status == "success" then
    return { text = "✓", hl = "SageFsSuccess" }
  elseif status == "error" then
    return { text = "✖", hl = "SageFsError" }
  elseif status == "running" then
    return { text = "⏳", hl = "SageFsRunning" }
  elseif status == "stale" then
    return { text = "~", hl = "SageFsStale" }
  else
    return { text = " ", hl = "Normal" }
  end
end

return M
