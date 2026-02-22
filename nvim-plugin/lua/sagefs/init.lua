-- sagefs/init.lua — Neovim integration for SageFs notebook experience
-- Connects pure modules (cells, format, model, sse) to Neovim APIs
local cells = require("sagefs.cells")
local format = require("sagefs.format")
local model = require("sagefs.model")
local sse_parser = require("sagefs.sse")

local M = {}

-- ─── Configuration ───────────────────────────────────────────────────────────

M.config = {
  port = 37749,
  auto_connect = true,
  highlight = {
    success = { fg = "#a6e3a1", italic = true },
    error = { fg = "#f38ba8", italic = true },
    output = { fg = "#a6adc8", italic = true },
    running = { fg = "#f9e2af" },
    stale = { fg = "#6c7086", italic = true },
  },
}

-- ─── State ───────────────────────────────────────────────────────────────────

M.state = model.new()
local ns = nil -- extmark namespace, created on setup()
local sse_job = nil
local sse_buffer = ""

-- ─── Helpers ─────────────────────────────────────────────────────────────────

local function base_url()
  return "http://localhost:" .. M.config.port
end

local function notify(msg, level)
  vim.notify("[SageFs] " .. msg, level or vim.log.levels.INFO)
end

-- ─── Highlight Setup ─────────────────────────────────────────────────────────

local function setup_highlights()
  local hl = M.config.highlight
  vim.api.nvim_set_hl(0, "SageFsSuccess", hl.success)
  vim.api.nvim_set_hl(0, "SageFsError", hl.error)
  vim.api.nvim_set_hl(0, "SageFsOutput", hl.output)
  vim.api.nvim_set_hl(0, "SageFsRunning", hl.running)
  vim.api.nvim_set_hl(0, "SageFsStale", hl.stale)
  vim.api.nvim_set_hl(0, "SageFsCellBorder", { fg = "#585b70" })
end

-- ─── Extmark Rendering ──────────────────────────────────────────────────────

local function clear_extmarks(buf)
  if ns then
    vim.api.nvim_buf_clear_namespace(buf, ns, 0, -1)
  end
end

local function render_cell_result(buf, boundary_line, cell_id)
  if not ns then return end
  local cell = model.get_cell_state(M.state, cell_id)
  if cell.status == "idle" then return end

  -- Gutter sign
  local sign = format.gutter_sign(cell.status)

  -- Inline result (on the ;; line)
  local inline = nil
  if cell.status == "success" or cell.status == "error" then
    inline = format.format_inline({
      ok = cell.status == "success" or cell.status == "stale",
      output = cell.output,
      error = cell.output,
    })
  end

  -- Virtual text on the boundary line
  local virt_text = {}
  if inline then
    table.insert(virt_text, { inline.text, inline.hl })
  end

  local opts = {
    id = cell_id * 1000, -- unique ID per cell
    virt_text = #virt_text > 0 and virt_text or nil,
    virt_text_pos = "eol",
    sign_text = sign.text,
    sign_hl_group = sign.hl,
    priority = 100,
  }

  -- boundary_line is 1-indexed, nvim_buf_set_extmark is 0-indexed
  pcall(vim.api.nvim_buf_set_extmark, buf, ns, boundary_line - 1, 0, opts)

  -- Virtual lines for expanded output
  if cell.status == "success" or cell.status == "error" then
    local vlines = format.format_virtual_lines({
      ok = cell.status == "success",
      output = cell.output,
      error = cell.output,
    })
    if #vlines > 0 then
      local virt_lines = {}
      for _, vl in ipairs(vlines) do
        table.insert(virt_lines, { { vl.text, vl.hl } })
      end
      pcall(vim.api.nvim_buf_set_extmark, buf, ns, boundary_line - 1, 0, {
        id = cell_id * 1000 + 1,
        virt_lines = virt_lines,
        virt_lines_above = false,
      })
    end
  end
end

local function render_all_extmarks(buf)
  if not ns then return end
  clear_extmarks(buf)

  local lines = vim.api.nvim_buf_get_lines(buf, 0, -1, false)
  local all_cells = cells.find_all_cells(lines)

  for _, cell in ipairs(all_cells) do
    render_cell_result(buf, cell.end_line, cell.id)
  end
end

-- ─── Flash Animation ─────────────────────────────────────────────────────────

local function flash_cell(buf, start_line, end_line)
  if not ns then return end
  local flash_ns = vim.api.nvim_create_namespace("sagefs_flash")

  for i = start_line, end_line do
    pcall(vim.api.nvim_buf_add_highlight, buf, flash_ns, "SageFsRunning", i - 1, 0, -1)
  end

  vim.defer_fn(function()
    pcall(vim.api.nvim_buf_clear_namespace, buf, flash_ns, 0, -1)
  end, 150)
end

-- ─── HTTP: POST /exec ────────────────────────────────────────────────────────

--- handle_result is the named seam the panel recommended.
--- Today it's called after sync POST; tomorrow from SSE handler.
local function handle_result(buf, cell_id, result)
  if result.ok then
    M.state = model.set_cell_state(M.state, cell_id, "success", result.output)
  else
    M.state = model.set_cell_state(M.state, cell_id, "error", result.error)
  end
  vim.schedule(function()
    render_all_extmarks(buf)
  end)
end

local function post_exec(code, buf, cell_id)
  local json_body = vim.fn.json_encode({ code = code })

  -- Use temp file for large payloads
  local use_temp = #json_body > 7000
  local temp_file = nil

  if use_temp then
    temp_file = vim.fn.tempname() .. ".json"
    local f = io.open(temp_file, "w")
    if f then
      f:write(json_body)
      f:close()
    end
  end

  local cmd
  if use_temp then
    cmd = {
      "curl", "-X", "POST", base_url() .. "/exec",
      "-H", "Content-Type: application/json",
      "-d", "@" .. temp_file,
      "--max-time", "60", "--silent", "--show-error",
    }
  else
    cmd = {
      "curl", "-X", "POST", base_url() .. "/exec",
      "-H", "Content-Type: application/json",
      "-d", json_body,
      "--max-time", "30", "--silent", "--show-error",
    }
  end

  local stdout_data = {}

  vim.fn.jobstart(cmd, {
    stdout_buffered = true,
    on_stdout = function(_, data)
      if data then stdout_data = data end
    end,
    on_exit = function(_, exit_code)
      vim.schedule(function()
        if temp_file then os.remove(temp_file) end

        if exit_code == 0 then
          local raw = table.concat(stdout_data, "\n")
          local result = format.parse_exec_response(raw)
          handle_result(buf, cell_id, result)
        else
          handle_result(buf, cell_id, { ok = false, error = "HTTP request failed" })
        end
      end)
    end,
  })
end

-- ─── Eval: <Alt-Enter> Handler ───────────────────────────────────────────────

function M.eval_cell()
  local buf = vim.api.nvim_get_current_buf()
  local cursor = vim.api.nvim_win_get_cursor(0)
  local cursor_line = cursor[1]

  local lines = vim.api.nvim_buf_get_lines(buf, 0, -1, false)
  local cell = cells.find_cell(lines, cursor_line)

  if not cell then
    notify("No cell found at cursor", vim.log.levels.WARN)
    return
  end

  local code = cells.prepare_code(cell.text)
  if not code then
    notify("Cell is empty", vim.log.levels.WARN)
    return
  end

  -- Find cell ID
  local all = cells.find_all_cells(lines)
  local cell_id = 1
  for _, c in ipairs(all) do
    if c.start_line == cell.start_line then
      cell_id = c.id
      break
    end
  end

  -- Mark as running + flash
  M.state = model.set_cell_state(M.state, cell_id, "running")
  render_all_extmarks(buf)
  flash_cell(buf, cell.start_line, cell.end_line)

  -- POST to SageFs
  post_exec(code, buf, cell_id)
end

-- ─── Eval visual selection ───────────────────────────────────────────────────

function M.eval_selection()
  vim.api.nvim_feedkeys(vim.api.nvim_replace_termcodes("<Esc>", true, false, true), "x", false)

  local start_pos = vim.fn.getpos("'<")
  local end_pos = vim.fn.getpos("'>")
  local buf = vim.api.nvim_get_current_buf()

  local sel_lines = vim.api.nvim_buf_get_lines(buf, start_pos[2] - 1, end_pos[2], false)
  local text = table.concat(sel_lines, "\n")
  local code = cells.prepare_code(text)

  if not code then
    notify("Selection is empty", vim.log.levels.WARN)
    return
  end

  -- Use cell_id 0 for ad-hoc selections
  M.state = model.set_cell_state(M.state, 0, "running")
  flash_cell(buf, start_pos[2], end_pos[2])
  post_exec(code, buf, 0)
end

-- ─── SSE Subscription ────────────────────────────────────────────────────────

local function start_sse()
  if sse_job then
    pcall(vim.fn.jobstop, sse_job)
  end
  sse_buffer = ""

  sse_job = vim.fn.jobstart(
    { "curl", "--no-buffer", "-N", base_url() .. "/events", "--silent" },
    {
      on_stdout = function(_, data, _)
        for _, chunk in ipairs(data) do
          sse_buffer = sse_buffer .. chunk .. "\n"
          local events, remainder = sse_parser.parse_chunk(sse_buffer)
          sse_buffer = remainder

          for _, event in ipairs(events) do
            if event.type == "state" and event.data then
              -- Server pushed state — update connection status
              M.state = model.set_status(M.state, "connected")
            end
          end
        end
      end,
      on_exit = function(_, code, _)
        sse_job = nil
        if code ~= 0 then
          M.state = model.set_status(M.state, "disconnected")
          -- Reconnect after delay
          vim.defer_fn(function()
            if M.config.auto_connect then
              start_sse()
            end
          end, 3000)
        end
      end,
    }
  )

  if sse_job and sse_job > 0 then
    M.state = model.set_status(M.state, "connected")
  end
end

local function stop_sse()
  if sse_job then
    pcall(vim.fn.jobstop, sse_job)
    sse_job = nil
  end
  M.state = model.set_status(M.state, "disconnected")
end

-- ─── Health Check ────────────────────────────────────────────────────────────

function M.health_check()
  local cmd = { "curl", "-s", "--max-time", "1", base_url() .. "/health" }
  local result = vim.fn.system(cmd)
  if result:match('"healthy"') then
    notify("Connected to SageFs on port " .. M.config.port)
    return true
  else
    notify("SageFs not available on port " .. M.config.port, vim.log.levels.ERROR)
    return false
  end
end

-- ─── Statusline Component ────────────────────────────────────────────────────

function M.statusline()
  local icon = M.state.status == "connected" and "⚡" or "💤"
  local cell_count = model.cell_count(M.state)
  if cell_count > 0 then
    return icon .. " SageFs [" .. cell_count .. "]"
  end
  return icon .. " SageFs"
end

-- ─── Commands & Keymaps ──────────────────────────────────────────────────────

local function register_commands()
  vim.api.nvim_create_user_command("SageFsEval", function()
    M.eval_cell()
  end, { desc = "Evaluate current cell" })

  vim.api.nvim_create_user_command("SageFsClear", function()
    M.state = model.clear_cells(M.state)
    local buf = vim.api.nvim_get_current_buf()
    clear_extmarks(buf)
    notify("Cleared all results")
  end, { desc = "Clear all cell results" })

  vim.api.nvim_create_user_command("SageFsConnect", function()
    if M.health_check() then
      start_sse()
    end
  end, { desc = "Connect to SageFs" })

  vim.api.nvim_create_user_command("SageFsDisconnect", function()
    stop_sse()
    notify("Disconnected")
  end, { desc = "Disconnect from SageFs" })

  vim.api.nvim_create_user_command("SageFsStatus", function()
    M.health_check()
  end, { desc = "Check SageFs status" })
end

local function register_keymaps()
  -- Alt-Enter: evaluate current cell
  vim.keymap.set("n", "<A-CR>", function() M.eval_cell() end,
    { desc = "SageFs: Evaluate cell", silent = true })

  -- Alt-Enter in visual: evaluate selection
  vim.keymap.set("v", "<A-CR>", function() M.eval_selection() end,
    { desc = "SageFs: Evaluate selection", silent = true })

  -- Leader mappings
  vim.keymap.set("n", "<leader>se", function() M.eval_cell() end,
    { desc = "SageFs: Evaluate cell", silent = true })
  vim.keymap.set("n", "<leader>sc", function()
    M.state = model.clear_cells(M.state)
    clear_extmarks(vim.api.nvim_get_current_buf())
  end, { desc = "SageFs: Clear results", silent = true })
  vim.keymap.set("n", "<leader>ss", function() M.health_check() end,
    { desc = "SageFs: Check status", silent = true })
end

local function register_autocmds()
  local group = vim.api.nvim_create_augroup("SageFs", { clear = true })

  -- Mark cells as stale when buffer changes
  vim.api.nvim_create_autocmd("TextChanged", {
    group = group,
    pattern = "*.fsx",
    callback = function(ev)
      M.state = model.mark_all_stale(M.state)
      render_all_extmarks(ev.buf)
    end,
  })

  -- Re-render extmarks when entering an .fsx buffer
  vim.api.nvim_create_autocmd("BufEnter", {
    group = group,
    pattern = "*.fsx",
    callback = function(ev)
      render_all_extmarks(ev.buf)
    end,
  })
end

-- ─── Setup ───────────────────────────────────────────────────────────────────

function M.setup(opts)
  opts = opts or {}
  M.config = vim.tbl_deep_extend("force", M.config, opts)
  M.config.port = tonumber(vim.env.SAGEFS_MCP_PORT) or M.config.port

  ns = vim.api.nvim_create_namespace("sagefs")

  setup_highlights()
  register_commands()
  register_keymaps()
  register_autocmds()

  -- Auto-connect
  if M.config.auto_connect then
    vim.defer_fn(function()
      if M.health_check() then
        start_sse()
      end
    end, 500)
  end

  -- Export for statusline
  _G.SageFs = M
end

return M
