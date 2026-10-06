@echo off
setlocal EnableExtensions DisableDelayedExpansion
set "ROOT=%~dp0"
set "SIDETALK_BUILD_SCRIPT=%ROOT%scripts\build.ps1"
set "SIDETALK_DOTNET_SCRIPT=%ROOT%scripts\ensure-dotnet.ps1"
set "SIDETALK_INSTALL_SCRIPT=%ROOT%scripts\install-local.ps1"
set "SIDETALK_INSTALL_FLAGS="
set "DOTNET_RESULT="
set "NO_LAUNCH="
set "NO_INSTALL="
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
if /I "%~1"=="-NoInstall" (
    set "NO_INSTALL=1"
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
if not exist "%SIDETALK_DOTNET_SCRIPT%" (
    echo Missing scripts\ensure-dotnet.ps1. Keep this file in the SIDETALK repository root.
    set "RC=1"
    goto finish
)
if not exist "%SIDETALK_INSTALL_SCRIPT%" (
    echo Missing scripts\install-local.ps1. Keep this file in the SIDETALK repository root.
    set "RC=1"
    goto finish
)
set "PWSH="
for %%I in (pwsh.exe) do set "PWSH=%%~$PATH:I"
if not defined PWSH (
    echo PowerShell 7 ^(pwsh.exe^) is required on PATH for building.
    echo Nothing was installed or stopped.
    set "RC=1"
    goto finish
)
rem pushd does not reset ERRORLEVEL on success, so test its own result with ||.
pushd "%ROOT%" || (
    echo Cannot enter the SIDETALK repository directory.
    set "RC=1"
    goto finish
)
set "PUSHED=1"

echo Checking the SDK selected by global.json...
set "DOTNET_RESULT=%TEMP%\sidetalk-dotnet-%RANDOM%-%RANDOM%.txt"
"%PWSH%" -NoLogo -NoProfile -ExecutionPolicy RemoteSigned -File "%SIDETALK_DOTNET_SCRIPT%" -ResultPath "%DOTNET_RESULT%"
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" (
    echo SIDETALK could not prepare the required .NET SDK automatically.
    goto finish
)
if not exist "%DOTNET_RESULT%" (
    echo SDK setup returned success, but no dotnet path was produced.
    set "RC=1"
    goto finish
)
set /p "DOTNET="<"%DOTNET_RESULT%"
del /q "%DOTNET_RESULT%" >nul 2>&1
set "DOTNET_RESULT="
if not defined DOTNET (
    echo SDK setup returned an empty dotnet path.
    set "RC=1"
    goto finish
)
if not exist "%DOTNET%" (
    echo SDK setup selected a missing dotnet executable: "%DOTNET%"
    set "RC=1"
    goto finish
)
for %%I in ("%DOTNET%") do set "DOTNET_DIR=%%~dpI"
set "DOTNET_ROOT=%DOTNET_DIR%"
set "PATH=%DOTNET_DIR%;%PATH%"
"%DOTNET%" --version
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" (
    echo The automatically selected SDK still does not satisfy global.json.
    goto finish
)
set "RC=0"

echo Packaging SIDETALK. The installed SIDETALK is not changed until Setup runs.
echo For code-only checks: pwsh -File scripts\verify.ps1
echo KakaoTalk and Sandboxie are not stopped by this entry point.
rem Use -Command so a cancelled PowerShell build cannot report success as -File can.
rem Pass the path as data, not interpolated PowerShell source. Policy is session-only.
"%PWSH%" -NoLogo -NoProfile -ExecutionPolicy RemoteSigned -Command "try { & $env:SIDETALK_BUILD_SCRIPT -SelfContained; if (-not $?) { if ($LASTEXITCODE) { exit $LASTEXITCODE }; exit 1 }; exit 0 } catch { Write-Error $_; exit 1 }"
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" (
    echo Build failed or was cancelled. Setup was not built or started; the installed SIDETALK is unchanged.
    goto finish
)

if defined NO_INSTALL set "SIDETALK_INSTALL_FLAGS=-NoInstall"
if defined NO_LAUNCH if not defined NO_INSTALL set "SIDETALK_INSTALL_FLAGS=-NoLaunch"
rem install-local.ps1 builds SIDETALK-Setup.exe, then (unless -NoInstall) opens it and
rem waits. Launching is the Setup window's own last-page choice.
"%PWSH%" -NoLogo -NoProfile -ExecutionPolicy RemoteSigned -Command "try { $f = @{ NoInstall = ($env:SIDETALK_INSTALL_FLAGS -eq '-NoInstall'); NoLaunch = ($env:SIDETALK_INSTALL_FLAGS -eq '-NoLaunch') }; & $env:SIDETALK_INSTALL_SCRIPT @f; if (-not $?) { if ($LASTEXITCODE) { exit $LASTEXITCODE }; exit 1 }; exit $LASTEXITCODE } catch { Write-Error $_; exit 1 }"
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" (
    echo SIDETALK was not installed by this run. See the stage and guidance above.
    goto finish
)
goto finish

:help
echo Usage: build-local.cmd [-NoInstall] [-NoLaunch] [-NoPause]
echo Verifies and publishes the current checkout, builds artifacts\release\SIDETALK-Setup.exe,
echo then opens Setup to install or update SIDETALK in %%LOCALAPPDATA%%\Programs\SIDETALK.
echo Missing .NET SDK and Inno Setup compiler versions are prepared once in a private
echo %%LOCALAPPDATA%%\SIDETALK-BuildTools cache. No administrator rights are needed.
echo -NoInstall  Stop after SIDETALK-Setup.exe is built. Windows installation is unchanged
echo             and nothing is launched (-NoLaunch is then implied).
echo -NoLaunch   Install, but do not offer to start SIDETALK at the end of Setup.
echo -NoPause    Do not wait for a key after an error.
echo Cancelling Setup is reported as a failure (exit 62), never as success.
echo Code-only checks: pwsh -File scripts\verify.ps1
echo After installing, open SIDETALK from the Start Menu. See docs\LOCAL-RUN.md.
exit /b 0

:finish
if defined DOTNET_RESULT if exist "%DOTNET_RESULT%" del /q "%DOTNET_RESULT%" >nul 2>&1
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
