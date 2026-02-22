@echo off
REM Run busted tests for sagefs.nvim plugin
REM Sets LuaRocks paths so busted can find its modules

set LUA_PATH=C:\Users\WillEhrendreich\AppData\Roaming/luarocks/share/lua/5.1/?.lua;C:\Users\WillEhrendreich\AppData\Roaming/luarocks/share/lua/5.1/?/init.lua;C:\ProgramData\chocolatey\lib\luarocks\luarocks-2.4.4-win32\systree/share/lua/5.1/?.lua;C:\ProgramData\chocolatey\lib\luarocks\luarocks-2.4.4-win32\systree/share/lua/5.1/?/init.lua;C:\ProgramData\chocolatey\lib\luarocks\luarocks-2.4.4-win32/lua/?.lua;C:\ProgramData\chocolatey\lib\luarocks\luarocks-2.4.4-win32\lua\?.lua;C:\ProgramData\chocolatey\lib\luarocks\luarocks-2.4.4-win32\lua\?\init.lua;.\?.lua;C:\ProgramData\chocolatey\lib\luarocks\luarocks-2.4.4-win32\?.lua;C:\ProgramData\chocolatey\lib\luarocks\luarocks-2.4.4-win32\?\init.lua;;C:\Program Files (x86)\Lua\5.1\lua\?.luac
set LUA_CPATH=C:\Users\WillEhrendreich\AppData\Roaming/luarocks/lib/lua/5.1/?.dll;C:\ProgramData\chocolatey\lib\luarocks\luarocks-2.4.4-win32\systree/lib/lua/5.1/?.dll;.\?.dll;C:\ProgramData\chocolatey\lib\luarocks\luarocks-2.4.4-win32\?.dll;C:\ProgramData\chocolatey\lib\luarocks\luarocks-2.4.4-win32\loadall.dll;C:\ProgramData\chocolatey\lib\luarocks\luarocks-2.4.4-win32\clibs\?.dll;C:\ProgramData\chocolatey\lib\luarocks\luarocks-2.4.4-win32\clibs\loadall.dll;.\?51.dll;C:\ProgramData\chocolatey\lib\luarocks\luarocks-2.4.4-win32\?51.dll;C:\ProgramData\chocolatey\lib\luarocks\luarocks-2.4.4-win32\clibs\?51.dll

cd /d %~dp0
set LUA_PATH=%LUA_PATH%;%~dp0lua\?.lua;%~dp0lua\?\init.lua
busted %*
