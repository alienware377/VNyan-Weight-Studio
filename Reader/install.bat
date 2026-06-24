@echo off
setlocal EnableDelayedExpansion
REM ===========================================================================
REM  Weight Studio Reader - one-click build + install
REM
REM  1) compiles VNyanWeightStudioReader.dll with Unity's Roslyn compiler
REM  2) builds WeightStudioReader.vnobj (UI prefab AssetBundle) via Unity batchmode
REM  3) installs BOTH into C:\Program Files\VNyan\Items\Assemblies\WeightStudioReader
REM
REM  IMPORTANT: this script does NOT self-elevate the whole run. The project may
REM  live on a per-user virtual drive (e.g. pCloud / a mapped network drive) that
REM  is INVISIBLE to an elevated process, so a self-elevated cmd cannot even read
REM  its own script there and would crash instantly. Instead we build in your
REM  normal user context, stage the outputs to a local C:\ temp folder, then
REM  elevate ONLY a tiny copy helper (which reads from C:\, not the virtual drive).
REM ===========================================================================

set "PROJ=%~dp0"
set "VNYAN=C:\Program Files\VNyan"
set "MGD=%VNYAN%\VNyan_Data\Managed"
set "VNI=%MGD%\VNyanInterface.dll"
set "UBUILD=%PROJ%_unitybuild"
set "PLUGINS=%UBUILD%\Assets\Plugins"
set "DLL=%PLUGINS%\VNyanWeightStudioReader.dll"
set "VNOBJ=%UBUILD%\AssetBundles\WeightStudioReader.vnobj"
set "DST=%VNYAN%\Items\Assemblies\WeightStudioReader"
set "STAGE=%TEMP%\WeightStudioReaderInstall"
set "UNITY=C:\Program Files\Unity\Hub\Editor\2022.3.22f1\Editor\Unity.exe"

echo ============================================
echo  Weight Studio Reader - build + install
echo ============================================
echo  Project    : %PROJ%
echo  VNyan      : %VNYAN%
echo  Unity      : %UNITY%
echo  Install to : %DST%
echo.

if not exist "%MGD%\UnityEngine.dll" ( echo [ERROR] VNyan managed DLLs not found at "%MGD%". & goto :fail )
if not exist "%VNI%" ( echo [ERROR] VNyanInterface.dll not found at "%VNI%". & goto :fail )
if not exist "%UNITY%" ( echo [ERROR] Unity 2022.3.22f1 not found at "%UNITY%". Install it via Unity Hub. & goto :fail )

set "CSC="
for /d %%E in ("C:\Program Files\Unity\Hub\Editor\*") do (
    if exist "%%E\Editor\Data\Tools\Roslyn\csc.exe" if not defined CSC set "CSC=%%E\Editor\Data\Tools\Roslyn\csc.exe"
)
if not defined CSC ( echo [ERROR] No Unity Roslyn compiler (csc.exe) found under any installed editor. & goto :fail )
echo Using compiler: !CSC!
echo.

echo [1/4] Compiling VNyanWeightStudioReader.dll ...
if not exist "%PLUGINS%" mkdir "%PLUGINS%"
"!CSC!" -target:library -nostdlib+ -noconfig -optimize+ -out:"%DLL%" ^
 -reference:"%MGD%\mscorlib.dll" ^
 -reference:"%MGD%\netstandard.dll" ^
 -reference:"%MGD%\System.dll" ^
 -reference:"%MGD%\System.Core.dll" ^
 -reference:"%MGD%\UnityEngine.dll" ^
 -reference:"%MGD%\UnityEngine.CoreModule.dll" ^
 -reference:"%MGD%\UnityEngine.AnimationModule.dll" ^
 -reference:"%MGD%\UnityEngine.ImageConversionModule.dll" ^
 -reference:"%MGD%\Unity.Collections.dll" ^
 -reference:"%MGD%\UnityEngine.SharedInternalsModule.dll" ^
 -reference:"%MGD%\UnityEngine.UIModule.dll" ^
 -reference:"%MGD%\UnityEngine.UI.dll" ^
 -reference:"%MGD%\UnityEngine.TextRenderingModule.dll" ^
 -reference:"%VNI%" ^
 "%PROJ%VbxFormat.cs" "%PROJ%VbxImporter.cs" ^
 "%PROJ%WeightStudioReaderPlugin.cs" "%PROJ%WindowDrag.cs"
if not exist "%DLL%" ( echo [ERROR] DLL compile failed (see compiler output above). & goto :fail )
echo     OK.
echo.

echo [2/4] Building WeightStudioReader.vnobj via Unity batchmode (can take a few minutes) ...
if exist "%VNOBJ%" del /q "%VNOBJ%"
"%UNITY%" -batchmode -quit -projectPath "%UBUILD%" -executeMethod WeightStudioReaderBuild.Build -logFile "%UBUILD%\build.log"
if not exist "%VNOBJ%" ( echo [ERROR] .vnobj build failed. See "%UBUILD%\build.log". & goto :fail )
echo     OK.
echo.

echo [3/4] Staging outputs to a local folder ...
if not exist "%STAGE%" mkdir "%STAGE%"
copy /Y "%DLL%"   "%STAGE%\" >nul || goto :fail
copy /Y "%VNOBJ%" "%STAGE%\" >nul || goto :fail
if not exist "%PROJ%dist" mkdir "%PROJ%dist"
copy /Y "%DLL%"   "%PROJ%dist\" >nul
copy /Y "%VNOBJ%" "%PROJ%dist\" >nul
echo     OK.
echo.

echo [4/4] Installing into VNyan (an Administrator prompt will appear) ...
set "HELPER=%STAGE%\_elevated_copy.cmd"
> "%HELPER%" echo @echo off
>>"%HELPER%" echo if not exist "%DST%" mkdir "%DST%"
>>"%HELPER%" echo copy /Y "%STAGE%\VNyanWeightStudioReader.dll" "%DST%"
>>"%HELPER%" echo copy /Y "%STAGE%\WeightStudioReader.vnobj" "%DST%"

powershell -NoProfile -Command "Start-Process -FilePath '%HELPER%' -Verb RunAs -Wait"

if not exist "%DST%\VNyanWeightStudioReader.dll" ( echo [ERROR] Install copy failed (DLL not in place). & goto :fail )
if not exist "%DST%\WeightStudioReader.vnobj" ( echo [ERROR] Install copy failed (.vnobj not in place). & goto :fail )

echo.
echo ============================================
echo  Done. Installed to:
echo    %DST%
echo  Files: VNyanWeightStudioReader.dll, WeightStudioReader.vnobj
echo.
echo  (Re)start VNyan, open the Plugins window, click "Weight Studio Reader".
echo ============================================
pause
exit /b 0

:fail
echo.
echo *** Build/Install failed. ***
pause
exit /b 1
