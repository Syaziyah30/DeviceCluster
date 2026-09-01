@echo off
setlocal

REM ---------------------------------------------------------------------------
REM Runs DeviceClusterServiceApp against the ML prediction service.
REM
REM Double-click this file, or call it with arguments from a terminal:
REM     run-service-app.cmd A9998
REM     run-service-app.cmd A9998 --unattended
REM
REM The app reads ML_SERVICE_URL to find the service. Without it the app falls
REM back to http://127.0.0.1:8000 and looks on the local machine, which is why
REM double-clicking the .exe directly does not reach the server.
REM
REM If the server moves, change the one line below.
REM ---------------------------------------------------------------------------

set "ML_SERVICE_URL=http://128.100.8.213:8000"

REM Prefer a Release build, fall back to Debug.
set "EXE=%~dp0bin\Release\net10.0-windows\win-x64\DeviceClusterServiceApp.exe"
if not exist "%EXE%" set "EXE=%~dp0bin\Debug\net10.0-windows\win-x64\DeviceClusterServiceApp.exe"

if not exist "%EXE%" (
    echo.
    echo DeviceClusterServiceApp.exe was not found.
    echo Build the solution first, from the repository root:
    echo.
    echo     dotnet build DeviceCluster.slnx
    echo.
    pause
    exit /b 1
)

echo Service URL : %ML_SERVICE_URL%
echo Executable  : %EXE%
echo.

"%EXE%" %*

REM Keep the window open if the app failed, so the error stays readable.
if errorlevel 1 pause
