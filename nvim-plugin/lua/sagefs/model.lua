-- sagefs/model.lua — Global model for cell state management
-- Pure Lua, no vim API dependencies — fully testable with busted
local M = {}

--- Create a new empty model
---@return table
function M.new()
  return {
    cells = {},
    status = "disconnected",
  }
end

--- Count tracked cells
---@param m table
---@return number
function M.cell_count(m)
  local count = 0
  for _ in pairs(m.cells) do count = count + 1 end
  return count
end

--- Set cell evaluation state
---@param m table
---@param cell_id number
---@param status string "idle"|"running"|"success"|"error"|"stale"
---@param output string|nil
---@return table
function M.set_cell_state(m, cell_id, status, output)
  m.cells[cell_id] = {
    status = status,
    output = output,
  }
  return m
end

--- Get cell state (returns idle for unknown cells)
---@param m table
---@param cell_id number
---@return {status: string, output: string|nil}
function M.get_cell_state(m, cell_id)
  local cell = m.cells[cell_id]
  if not cell then
    return { status = "idle", output = nil }
  end
  return cell
end

--- Mark a cell as stale (only if it was success or error)
---@param m table
---@param cell_id number
---@return table
function M.mark_stale(m, cell_id)
  local cell = m.cells[cell_id]
  if cell and (cell.status == "success" or cell.status == "error") then
    cell.status = "stale"
  end
  return m
end

--- Mark all evaluated cells as stale
---@param m table
---@return table
function M.mark_all_stale(m)
  for id, cell in pairs(m.cells) do
    if cell.status == "success" or cell.status == "error" then
      cell.status = "stale"
    end
  end
  return m
end

--- Clear all cell state
---@param m table
---@return table
function M.clear_cells(m)
  m.cells = {}
  return m
end

--- Set connection status
---@param m table
---@param status string "connected"|"disconnected"|"reconnecting"
---@return table
function M.set_status(m, status)
  m.status = status
  return m
end

return M
