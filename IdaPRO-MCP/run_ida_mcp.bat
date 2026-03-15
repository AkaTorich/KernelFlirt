@echo off
set PYTHONPATH=%~dp0src
set IDA_MCP_LOG=%TEMP%\ida_mcp_server.log
echo [%date% %time%] bat started, PYTHONPATH=%PYTHONPATH% >> "%IDA_MCP_LOG%"
python -m ida_mcp.mcp_server --transport stdio --ida-host 127.0.0.1 --ida-port 13370
if errorlevel 1 echo [%date% %time%] python exited errorlevel 1 >> "%IDA_MCP_LOG%"
