@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"
set "MABI_NATIVE=%~dp0发布包\MabiAgent-NET验收版\MabiAgent"
if not exist "%MABI_NATIVE%\MabiAgent.exe" (
  echo Native package is missing. See README.md.
  exit /b 1
)
set "MABI_STATE=%~dp0data\agent-run\desktop"
if not exist "%MABI_STATE%\work-queue.json" (
  echo Existing task data is missing. See README.md.
  exit /b 1
)
start "" "%MABI_NATIVE%\MabiAgent.exe" %* --root "%MABI_NATIVE%" --state-dir "%MABI_STATE%"
