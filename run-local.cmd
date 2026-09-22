@echo off
setlocal EnableExtensions DisableDelayedExpansion
set "ROOT=%~dp0"
set "OUTPUT=%ROOT%artifacts\publish\win-x64"
set "NO_PAUSE="
set "BUILD_OPTIONS="
set "RC=0"

:arguments
if "%~1"=="" goto launch
if /I "%~1"=="-NoPause" (
    set "NO_PAUSE=1"
    set "BUILD_OPTIONS=-NoPause"
    shift
    goto arguments
)
if /I "%~1"=="-Help" goto help
if /I "%~1"=="/?" goto help
echo Unsupported option. Use run-local.cmd -Help.
set "RC=2"
goto finish

:launch
if not exist "%OUTPUT%\Sidetalk.exe" goto build
if not exist "%OUTPUT%\Sidetalk.dll" goto build
if not exist "%OUTPUT%\Sidetalk.Core.dll" goto build
if not exist "%OUTPUT%\Sidetalk.runtimeconfig.json" goto build
if not exist "%OUTPUT%\Sidetalk.deps.json" goto build
rem The existing build starts immediately; no SDK, PowerShell 7, or rebuild is needed.
rem This starts SIDETALK, not KakaoTalk or a Sandboxie command.
start "" /D "%OUTPUT%" "%OUTPUT%\Sidetalk.exe"
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" echo SIDETALK could not be started. See the error above.
goto finish

:build
if not exist "%ROOT%build-local.cmd" (
    echo No published app or build-local.cmd was found. Keep both CMD files in the repository root.
    set "RC=1"
    goto finish
)
echo No complete local build was found. Building once before opening SIDETALK...
rem Tail-chain without CALL: a percent sign in the repository path is not expanded twice.
"%ROOT%build-local.cmd" %BUILD_OPTIONS%
exit /b %ERRORLEVEL%

:help
echo Usage: run-local.cmd [-NoPause]
echo Opens the published SIDETALK launcher. If it is missing, builds once and opens it.
echo Source updates are applied only by build-local.cmd; this file does not rebuild every time.
echo -NoPause  Do not wait for a key after an error.
echo See docs\LOCAL-RUN.md.
exit /b 0

:finish
if "%RC%"=="0" exit /b 0
if defined NO_PAUSE exit /b %RC%
if defined CI exit /b %RC%
if defined GITHUB_ACTIONS exit /b %RC%
if defined TF_BUILD exit /b %RC%
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -Command "if ([Environment]::UserInteractive -and -not [Console]::IsInputRedirected) { exit 0 }; exit 1" >nul 2>&1
if not errorlevel 1 pause
exit /b %RC%
