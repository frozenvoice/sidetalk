@echo off
setlocal EnableExtensions DisableDelayedExpansion
set "ROOT=%~dp0"
set "OUTPUT=%ROOT%artifacts\publish\win-x64"
set "SIDETALK_BUILD_SCRIPT=%ROOT%scripts\build.ps1"
set "NO_LAUNCH="
set "NO_PAUSE="
set "PUSHED="
set "RC=0"

:arguments
if "%~1"=="" goto preflight
if /I "%~1"=="-NoLaunch" (
    set "NO_LAUNCH=1"
    shift
    goto arguments
)
if /I "%~1"=="-NoPause" (
    set "NO_PAUSE=1"
    shift
    goto arguments
)
if /I "%~1"=="-Help" goto help
if /I "%~1"=="/?" goto help
echo Unsupported option. Use build-local.cmd -Help.
set "RC=2"
goto finish

:preflight
if not exist "%SIDETALK_BUILD_SCRIPT%" (
    echo Missing scripts\build.ps1. Keep this file in the SIDETALK repository root.
    set "RC=1"
    goto finish
)
set "PWSH="
for %%I in (pwsh.exe) do set "PWSH=%%~$PATH:I"
if not defined PWSH (
    echo PowerShell 7 ^(pwsh.exe^) is required on PATH for building.
    echo Install PowerShell 7, reopen this file, and try again. Nothing was installed or stopped.
    set "RC=1"
    goto finish
)
set "DOTNET="
for %%I in (dotnet.exe) do set "DOTNET=%%~$PATH:I"
if not defined DOTNET (
    echo The .NET 10 SDK is required on PATH for building ^(not just the runtime^).
    echo See global.json for the SDK requirement. Nothing was installed or stopped.
    set "RC=1"
    goto finish
)
pushd "%ROOT%"
if errorlevel 1 (
    echo Cannot enter the SIDETALK repository directory.
    set "RC=1"
    goto finish
)
set "PUSHED=1"
echo Checking the SDK selected by global.json...
"%DOTNET%" --version
if errorlevel 1 (
    echo Install the .NET 10 SDK required by global.json, then try again.
    set "RC=1"
    goto finish
)

echo Packaging SIDETALK. Close SIDETALK and its published IsolationProbe before replacement.
echo For code-only checks with the published app running: pwsh -File scripts\verify.ps1
echo KakaoTalk and Sandboxie are not stopped by this entry point.
rem Use -Command so a cancelled PowerShell build cannot report success as -File can.
rem Pass the path as data, not interpolated PowerShell source. Policy is session-only.
"%PWSH%" -NoLogo -NoProfile -ExecutionPolicy RemoteSigned -Command "try { & $env:SIDETALK_BUILD_SCRIPT -SelfContained; if (-not $?) { if ($LASTEXITCODE) { exit $LASTEXITCODE }; exit 1 }; exit 0 } catch { Write-Error $_; exit 1 }"
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" (
    echo Build failed or was cancelled. The previous app will not be launched as a fallback.
    goto finish
)
if not exist "%OUTPUT%\Sidetalk.exe" (
    echo Build returned success, but the published Sidetalk.exe was not found.
    set "RC=1"
    goto finish
)
echo Build complete: "%OUTPUT%\Sidetalk.exe"
if defined NO_LAUNCH goto finish
rem Open the SIDETALK launcher only. KakaoTalk still requires its GUI confirmation.
start "" /D "%OUTPUT%" "%OUTPUT%\Sidetalk.exe"
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" echo SIDETALK could not be started. See the error above.
goto finish

:help
echo Usage: build-local.cmd [-NoLaunch] [-NoPause]
echo Builds the current checkout using scripts\build.ps1 -SelfContained, then opens SIDETALK.
echo -NoLaunch  Build only; do not open SIDETALK.
echo -NoPause   Do not wait for a key after an error.
echo Code-only checks: pwsh -File scripts\verify.ps1
echo For everyday use, double-click run-local.cmd. See docs\LOCAL-RUN.md.
exit /b 0

:finish
if defined PUSHED popd
if "%RC%"=="0" exit /b 0
if defined NO_PAUSE exit /b %RC%
if defined CI exit /b %RC%
if defined GITHUB_ACTIONS exit /b %RC%
if defined TF_BUILD exit /b %RC%
rem Keep double-click errors visible without hanging redirected or unattended callers.
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -Command "if ([Environment]::UserInteractive -and -not [Console]::IsInputRedirected) { exit 0 }; exit 1" >nul 2>&1
if not errorlevel 1 pause
exit /b %RC%
