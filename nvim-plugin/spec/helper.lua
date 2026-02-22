-- Test helper: mock vim APIs for busted tests running outside Neovim
-- This lets us test pure Lua logic without needing a running Neovim instance

-- Only create mocks if vim global doesn't exist (i.e., running under busted, not Neovim)
if not vim then
  _G.vim = {
    -- Minimal vim.fn mocks
    fn = {
      json_encode = function(t)
        -- Simple JSON encoder for test use
        local json = require("dkjson") or error("dkjson not available")
        return json.encode(t)
      end,
      json_decode = function(s)
        local json = require("dkjson") or error("dkjson not available")
        return json.decode(s)
      end,
    },
    -- vim.json (Neovim's built-in JSON)
    json = {
      encode = function(t) return vim.fn.json_encode(t) end,
      decode = function(s) return vim.fn.json_decode(s) end,
    },
    -- vim.split
    split = function(s, sep, opts)
      opts = opts or {}
      local result = {}
      local pattern = opts.plain and sep or sep
      if opts.plain then
        local start = 1
        while true do
          local i, j = s:find(pattern, start, true)
          if not i then
            table.insert(result, s:sub(start))
            break
          end
          table.insert(result, s:sub(start, i - 1))
          start = j + 1
        end
      else
        for part in s:gmatch("([^" .. sep .. "]*)") do
          table.insert(result, part)
        end
      end
      return result
    end,
    -- vim.log.levels
    log = {
      levels = {
        DEBUG = 0,
        INFO = 1,
        WARN = 2,
        ERROR = 3,
      },
    },
    -- vim.notify stub (captures calls for assertion)
    _notifications = {},
    notify = function(msg, level)
      table.insert(vim._notifications, { msg = msg, level = level })
    end,
    -- vim.schedule stub (runs immediately in tests)
    schedule = function(fn) fn() end,
    schedule_wrap = function(fn) return fn end,
    -- vim.loop / vim.uv stubs
    loop = {
      hrtime = function() return os.clock() * 1e9 end,
    },
    uv = {
      hrtime = function() return os.clock() * 1e9 end,
    },
    -- vim.api stubs (per-test, override as needed)
    api = {},
    -- vim.env stub
    env = {},
    -- vim.g stub
    g = {},
    -- vim.b stub (buffer-local vars)
    b = setmetatable({}, {
      __index = function(_, _) return {} end,
    }),
  }
end

-- Add the plugin lua directory to package.path so require("sagefs.cells") works
local plugin_root = debug.getinfo(1, "S").source:match("@(.*/)")
  or debug.getinfo(1, "S").source:match("@(.*\\)")
  or "./"
-- Go up from spec/ to nvim-plugin/lua/
local lua_root = plugin_root .. "../lua/"
package.path = lua_root .. "?.lua;" .. lua_root .. "?/init.lua;" .. package.path
