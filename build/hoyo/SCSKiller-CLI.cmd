@echo off
setlocal
set "SCSKILLER_DATA_DIR=%~dp0data"
set "SCSKILLER_TEST_RECORDER=0"
"%~dp0cli\scskiller.exe" %*
exit /b %errorlevel%
