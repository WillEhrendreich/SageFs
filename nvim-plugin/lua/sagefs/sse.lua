-- sagefs/sse.lua — SSE message parser
-- Pure parsing logic, no vim API dependencies — fully testable with busted
local M = {}

--- Parse a chunk of SSE text into events and unconsumed remainder
--- Events are delimited by blank lines (\n\n)
--- Each event can have: event: <type>, data: <payload>, : <comment>
---@param chunk string
---@return {type: string?, data: string?}[], string remainder
function M.parse_chunk(chunk)
  local events = {}
  local remainder = ""

  if not chunk or chunk == "" then
    return events, ""
  end

  -- Split on double-newline (event boundary)
  -- Handle both \n\n and \r\n\r\n
  local normalized = chunk:gsub("\r\n", "\n")

  -- Find all complete events (terminated by \n\n)
  local pos = 1
  while true do
    local boundary = normalized:find("\n\n", pos, true)
    if not boundary then
      -- No more complete events; rest is remainder
      remainder = normalized:sub(pos)
      break
    end

    local event_text = normalized:sub(pos, boundary - 1)
    pos = boundary + 2

    -- Parse the event lines
    local event_type = nil
    local data_parts = {}

    for line in (event_text .. "\n"):gmatch("([^\n]*)\n") do
      if line:match("^:") then
        -- Comment, ignore
      elseif line:match("^event: ") then
        event_type = line:match("^event: (.+)")
      elseif line:match("^data: ") then
        table.insert(data_parts, line:match("^data: (.+)"))
      elseif line:match("^data:$") then
        table.insert(data_parts, "")
      end
    end

    local data = nil
    if #data_parts > 0 then
      data = table.concat(data_parts, "\n")
    end

    table.insert(events, { type = event_type, data = data })
  end

  return events, remainder
end

return M
