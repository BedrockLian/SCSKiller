@echo off
setlocal
set "SCSKILLER_DATA_DIR=%~dp0data"
set "SCSKILLER_TEST_RECORDER=0"
start "" /D "%~dp0" "%~dp0SCSKiller.exe" %*
