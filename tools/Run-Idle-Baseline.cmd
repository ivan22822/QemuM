@echo off
setlocal
cd /d "%~dp0.."
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set OTEL_SDK_DISABLED=true
set APPLICATIONINSIGHTS_ENABLE_AGENT=false
set COR_ENABLE_PROFILING=0
set CORECLR_ENABLE_PROFILING=0
set POWERSHELL_TELEMETRY_OPTOUT=1
set OTEL_TRACES_EXPORTER=none
set OTEL_METRICS_EXPORTER=none
set OTEL_LOGS_EXPORTER=none
set APPLICATIONINSIGHTS_CONNECTION_STRING=
set APPINSIGHTS_INSTRUMENTATIONKEY=
if exist "%LOCALAPPDATA%\Programs\Python\Python312\python.exe" (
 "%LOCALAPPDATA%\Programs\Python\Python312\python.exe" "%~dp0run_idle_baseline_independent.py"
) else (
 py -3 "%~dp0run_idle_baseline_independent.py"
)
if errorlevel 1 pause
endlocal
