@echo off
setlocal
REM Installs the SDK (docked-window) build: DLL + .vnobj into VNyan.
REM Self-elevate to Administrator if needed (C:\Program Files is protected).
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Requesting Administrator permission...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

set "SRC=%~dp0dist"
set "DST=C:\Program Files\VNyan\Items\Assemblies\WeightStudio"

if not exist "%SRC%\VNyanWeightStudio.dll" (
    echo [!] dist\VNyanWeightStudio.dll missing. Run build_vnobj.bat first.
    goto :fail
)
if not exist "%SRC%\WeightStudio.vnobj" (
    echo [!] dist\WeightStudio.vnobj missing. Run build_vnobj.bat first.
    goto :fail
)

echo Installing Weight Studio into VNyan...
if not exist "%DST%" mkdir "%DST%"
copy /Y "%SRC%\VNyanWeightStudio.dll" "%DST%\"  || goto :fail
copy /Y "%SRC%\WeightStudio.vnobj"    "%DST%\"  || goto :fail

echo.
echo Done. Installed to:
echo   %DST%
echo.
echo (Re)start VNyan, open the Plugins window, and click "Weight Studio".
pause
exit /b

:fail
echo.
echo *** Install failed. Make sure VNyan is at C:\Program Files\VNyan. ***
pause
exit /b 1
