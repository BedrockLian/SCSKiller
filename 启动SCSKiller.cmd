@echo off
setlocal
if not defined SCSKILLER_DATA_DIR set "SCSKILLER_DATA_DIR=%~dp0out\app-data"
set "SCSKILLER_TEST_RECORDER=0"
set "SCSKILLER_DEV_EXE=%~dp0src\SCSKiller.Ui\bin\Release\net10.0-windows10.0.26100.0\win-x64\SCSKiller.exe"
if not exist "%SCSKILLER_DEV_EXE%" (
  echo Build the Windows Avalonia app first. See docs\hoyo-readers.md.
  pause
  exit /b 1
)
start "" /D "%~dp0" "%SCSKILLER_DEV_EXE%" %*
