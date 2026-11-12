@echo off
rem ---------------------------------------------------------------------------
rem Mosytool packaging entry point. The real work lives in publish.ps1.
rem ASCII only: cmd.exe reads .bat with the active OEM code page (936 = zh-CN),
rem and non-ASCII comments can swallow the CRLF and execute garbage.
rem
rem   publish.bat               package normally
rem   publish.bat -NoPause      no key press at the end (scripted runs)
rem   publish.bat -SkipNative   skip penumbra_ffi.dll / qualcomm_ffi.dll
rem   publish.bat -KeepDist     keep the existing dist folder
rem
rem Anything that locks files inside this folder (the running app, adb, scrcpy)
rem is force-terminated by publish.ps1 before cleaning.
rem ---------------------------------------------------------------------------

setlocal
cd /d "%~dp0"

set "PS=powershell.exe"
where pwsh.exe >nul 2>&1 && set "PS=pwsh.exe"

%PS% -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1" %*
set "RC=%ERRORLEVEL%"

rem publish.ps1 pauses on both success and failure; only add a stop here when it
rem returned an error so a double click never closes the window silently.
if not "%RC%"=="0" (
    if /i "%~1"=="-NoPause" goto :finish
    echo.
    echo Publish failed with exit code %RC%.
    pause
)

:finish
endlocal & exit /b %RC%
