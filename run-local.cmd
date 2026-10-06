@echo off
setlocal EnableExtensions DisableDelayedExpansion
set "ROOT=%~dp0"
set "INSTALLED=%LOCALAPPDATA%\Programs\SIDETALK"
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
if not defined LOCALAPPDATA goto build
if not exist "%INSTALLED%\Sidetalk.exe" goto build
if not exist "%INSTALLED%\Sidetalk.dll" goto build
if not exist "%INSTALLED%\unins000.exe" goto build
rem Opens the installed SIDETALK (same as the Start Menu entry). No SDK, rebuild or
rem PowerShell is needed. This starts SIDETALK, not KakaoTalk or a Sandboxie command.
start "" /D "%INSTALLED%" "%INSTALLED%\Sidetalk.exe"
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" echo SIDETALK could not be started. See the error above.
goto finish

:build
if not exist "%ROOT%build-local.cmd" (
    echo SIDETALK is not installed and build-local.cmd was not found next to this file.
    set "RC=1"
    goto finish
)
rem Never falls back to artifacts\publish and never creates or resets a profile:
rem the only answer to "not installed" is the normal Setup path.
echo SIDETALK is not installed. Building and opening Setup...
rem Tail-chain without CALL: a percent sign in the repository path is not expanded twice.
"%ROOT%build-local.cmd" %BUILD_OPTIONS%
exit /b %ERRORLEVEL%

:help
echo Usage: run-local.cmd [-NoPause]
echo Opens the installed SIDETALK from %%LOCALAPPDATA%%\Programs\SIDETALK.
echo If SIDETALK is not installed, runs build-local.cmd to build and open SIDETALK-Setup.exe.
echo Source updates are applied only by build-local.cmd; this file does not rebuild every time.
echo Everyday use: Start Menu - SIDETALK. See docs\LOCAL-RUN.md.
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
