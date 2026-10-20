@echo off
setlocal
if "%~1"=="" goto gui
py -3 -c "import sys; sys.exit(sys.version_info < (3,8))" >nul 2>&1
if not errorlevel 1 goto pylauncher
python -c "import sys; sys.exit(sys.version_info < (3,8))" >nul 2>&1
if not errorlevel 1 goto pythonlauncher
echo Python 3.8 or newer is required. Install Python 3 and enable its launcher or PATH option.
exit /b 1
:pylauncher
py -3 "%~dp0vm.py" %*
exit /b %errorlevel%
:pythonlauncher
python "%~dp0vm.py" %*
exit /b %errorlevel%
:gui
if not exist "%~dp0VMManager.exe" (
  echo VM Manager application is missing. Restore the program files.
  exit /b 1
)
start "" /b "%~dp0VMManager.exe"
exit /b 0
