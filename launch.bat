@echo off
rem Lookback launcher: updates, builds and starts the desktop app, from the repository root.
rem
rem   launch.bat              update main (only when it is checked out with no local changes),
rem                           build Release, install the app's packages if they changed, start the app
rem   launch.bat --no-pull    skip the update
rem   launch.bat --no-build   skip the .NET build (start what is already built)
rem   launch.bat --no-start   everything but starting the app (a build check)
rem
rem Needs the .NET 10 SDK, Node.js 24 and git on PATH (ffmpeg too, for footage).
rem Keep this file current: a change to how Lookback is built or started updates it in the same PR (see CLAUDE.md).

setlocal EnableExtensions
cd /d "%~dp0"

set "PULL=1"
set "BUILD=1"
set "START=1"

:args
if "%~1"=="" goto args_done
if /i "%~1"=="--no-pull" (set "PULL=0" & shift & goto args)
if /i "%~1"=="--no-build" (set "BUILD=0" & shift & goto args)
if /i "%~1"=="--no-start" (set "START=0" & shift & goto args)
if /i "%~1"=="--help" goto usage
if /i "%~1"=="-h" goto usage
if /i "%~1"=="/?" goto usage
echo [launch] Unknown option: %~1
echo.
call :print_usage
exit /b 2

:args_done

rem ---- What it needs ----
where dotnet >nul 2>&1 || (echo [launch] The .NET SDK was not found. Install the .NET 10 SDK from https://dotnet.microsoft.com/download & goto fail)
where npm >nul 2>&1 || (echo [launch] Node.js was not found. Install Node.js 24 from https://nodejs.org & goto fail)

rem ---- Not while Lookback from this folder is running: the build cannot replace a running recorder ----
powershell -NoProfile -Command "$root = (Get-Location).Path + '\'; if (Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) }) { exit 1 }"
if errorlevel 1 (echo [launch] Lookback from this folder is already running. Close it first. & goto fail)

rem ---- Update main from GitHub ----
if "%PULL%"=="0" goto pull_done
where git >nul 2>&1 || (echo [launch] git was not found: skipping the update. & goto pull_done)
set "BRANCH="
for /f "delims=" %%b in ('git rev-parse --abbrev-ref HEAD 2^>nul') do set "BRANCH=%%b"
set "DIRTY="
for /f "delims=" %%s in ('git status --porcelain -uno 2^>nul') do set "DIRTY=1"
if /i not "%BRANCH%"=="main" (echo [launch] On branch %BRANCH%, not main: skipping the update. & goto pull_done)
if defined DIRTY (echo [launch] main has local changes: skipping the update. & goto pull_done)
echo [launch] Updating main from GitHub...
git pull --ff-only
if errorlevel 1 echo [launch] Could not update main: offline, or it has diverged from GitHub. Using what is here.
:pull_done

rem ---- Build the recorder and tools ----
if "%BUILD%"=="0" goto build_done
echo [launch] Building Lookback (Release)...
dotnet build Lookback.slnx -c Release -nologo -v minimal
if errorlevel 1 (echo [launch] The build failed; see above. & goto fail)
:build_done

rem ---- The app's packages: install when missing or when package-lock.json changed ----
set "INSTALL=0"
if not exist "app\node_modules\.package-lock.json" set "INSTALL=1"
if "%INSTALL%"=="1" goto install
powershell -NoProfile -Command "if ((Get-Item 'app\package-lock.json').LastWriteTimeUtc -gt (Get-Item 'app\node_modules\.package-lock.json').LastWriteTimeUtc) { exit 1 }"
if errorlevel 1 goto install
goto install_done
:install
echo [launch] Installing the app's packages (npm ci)...
pushd app
call npm ci --no-audit --no-fund
if errorlevel 1 (popd & echo [launch] npm ci failed; see above. & goto fail)
popd
:install_done

rem ---- Start ----
if "%START%"=="0" (echo [launch] Ready; not starting the app ^(--no-start^). & exit /b 0)
echo [launch] Starting Lookback...
pushd app
call npm start
set "RC=%ERRORLEVEL%"
popd
exit /b %RC%

:usage
call :print_usage
exit /b 0

:print_usage
echo Usage: launch.bat [--no-pull] [--no-build] [--no-start]
echo.
echo   Updates main (only when it is checked out with no local changes), builds Release,
echo   installs the app's packages if they changed, and starts the desktop app.
echo.
echo   --no-pull    skip the update
echo   --no-build   skip the .NET build
echo   --no-start   everything but starting the app
exit /b 0

:fail
echo [launch] Stopped.
if defined LAUNCH_NO_PAUSE exit /b 1
rem Keep the window open when started by double-click, so the message can be read.
echo %cmdcmdline% | find /i "/c" >nul && pause
exit /b 1
