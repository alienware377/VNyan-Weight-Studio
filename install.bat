@echo off
setlocal EnableDelayedExpansion
REM ===========================================================================
REM  Weight Studio - one-click build + install
REM
REM  1) compiles VNyanWeightStudio.dll with Unity's Roslyn compiler
REM  2) builds WeightStudio.vnobj (UI prefab AssetBundle) via Unity batchmode
REM  3) installs BOTH into C:\Program Files\VNyan\Items\Assemblies\WeightStudio
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
set "DLL=%PLUGINS%\VNyanWeightStudio.dll"
set "VNOBJ=%UBUILD%\AssetBundles\WeightStudio.vnobj"
set "DST=%VNYAN%\Items\Assemblies\WeightStudio"
set "STAGE=%TEMP%\WeightStudioInstall"
set "UNITY=C:\Program Files\Unity\Hub\Editor\2022.3.22f1\Editor\Unity.exe"

echo ============================================
echo  Weight Studio - build + install
echo ============================================
echo  Project    : %PROJ%
echo  VNyan      : %VNYAN%
echo  Unity      : %UNITY%
echo  Install to : %DST%
echo.

if not exist "%MGD%\UnityEngine.dll" ( echo [ERROR] VNyan managed DLLs not found at "%MGD%". & goto :fail )
if not exist "%VNI%" ( echo [ERROR] VNyanInterface.dll not found at "%VNI%". & goto :fail )
if not exist "%UNITY%" ( echo [ERROR] Unity 2022.3.22f1 not found at "%UNITY%". Install it via Unity Hub. & goto :fail )

REM --- locate Unity's standalone Roslyn compiler (any installed editor) ---
set "CSC="
for /d %%E in ("C:\Program Files\Unity\Hub\Editor\*") do (
    if exist "%%E\Editor\Data\Tools\Roslyn\csc.exe" if not defined CSC set "CSC=%%E\Editor\Data\Tools\Roslyn\csc.exe"
)
if not defined CSC ( echo [ERROR] No Unity Roslyn compiler (csc.exe) found under any installed editor. & goto :fail )
echo Using compiler: !CSC!
echo.

echo [1/4] Compiling VNyanWeightStudio.dll ...
if not exist "%PLUGINS%" mkdir "%PLUGINS%"
"!CSC!" -target:library -nostdlib+ -noconfig -optimize+ -out:"%DLL%" ^
 -reference:"%MGD%\mscorlib.dll" ^
 -reference:"%MGD%\netstandard.dll" ^
 -reference:"%MGD%\System.dll" ^
 -reference:"%MGD%\System.Core.dll" ^
 -reference:"%MGD%\UnityEngine.dll" ^
 -reference:"%MGD%\UnityEngine.CoreModule.dll" ^
 -reference:"%MGD%\UnityEngine.AnimationModule.dll" ^
 -reference:"%MGD%\UnityEngine.PhysicsModule.dll" ^
 -reference:"%MGD%\UnityEngine.IMGUIModule.dll" ^
 -reference:"%MGD%\UnityEngine.InputLegacyModule.dll" ^
 -reference:"%MGD%\UnityEngine.ImageConversionModule.dll" ^
 -reference:"%MGD%\UnityEngine.AssetBundleModule.dll" ^
 -reference:"%MGD%\Unity.Collections.dll" ^
 -reference:"%MGD%\UnityEngine.SharedInternalsModule.dll" ^
 -reference:"%MGD%\UnityEngine.UIModule.dll" ^
 -reference:"%MGD%\UnityEngine.UI.dll" ^
 -reference:"%MGD%\UnityEngine.TextRenderingModule.dll" ^
 -reference:"%MGD%\Newtonsoft.Json.dll" ^
 -reference:"%VNI%" ^
 "%PROJ%WeightModel.cs" "%PROJ%WeightTools.cs" "%PROJ%VbxFormat.cs" ^
 "%PROJ%VbxExporter.cs" "%PROJ%GltfVrmExporter.cs" ^
 "%PROJ%WeightHeatmap.cs" "%PROJ%WeightTransfer.cs" ^
 "%PROJ%ColliderGen.cs" "%PROJ%CollidersExport.cs" "%PROJ%UnityPackageExport.cs" ^
 "%PROJ%VsfAvatarReader.cs" ^
 "%PROJ%BoneWeightStudioPlugin.cs" "%PROJ%WindowDrag.cs" "%PROJ%NativeFileDialog.cs"
if not exist "%DLL%" ( echo [ERROR] DLL compile failed (see compiler output above). & goto :fail )
echo     OK.
echo.

echo [2/4] Building WeightStudio.vnobj via Unity batchmode (can take a few minutes) ...
if exist "%VNOBJ%" del /q "%VNOBJ%"
"%UNITY%" -batchmode -quit -projectPath "%UBUILD%" -executeMethod WeightStudioBuild.Build -logFile "%UBUILD%\build.log"
if not exist "%VNOBJ%" ( echo [ERROR] .vnobj build failed. See "%UBUILD%\build.log". & goto :fail )
echo     OK.
echo.

echo [3/4] Staging outputs to a local folder ...
REM Stage to C:\ so the elevated copy step can read them even if the project
REM lives on a per-user virtual drive that elevation can't see.
if not exist "%STAGE%" mkdir "%STAGE%"
copy /Y "%DLL%"   "%STAGE%\" >nul || goto :fail
copy /Y "%VNOBJ%" "%STAGE%\" >nul || goto :fail
if not exist "%PROJ%dist" mkdir "%PROJ%dist"
copy /Y "%DLL%"   "%PROJ%dist\" >nul
copy /Y "%VNOBJ%" "%PROJ%dist\" >nul
echo     OK.
echo.

echo [4/4] Installing into VNyan (an Administrator prompt will appear) ...
REM Generate a self-contained copy helper ON C:\ and run it elevated. Both its
REM source (STAGE) and destination (DST) are on C:\, so it works regardless of
REM where this project lives.
set "HELPER=%STAGE%\_elevated_copy.cmd"
> "%HELPER%" echo @echo off
>>"%HELPER%" echo if not exist "%DST%" mkdir "%DST%"
>>"%HELPER%" echo copy /Y "%STAGE%\VNyanWeightStudio.dll" "%DST%"
>>"%HELPER%" echo copy /Y "%STAGE%\WeightStudio.vnobj" "%DST%"

powershell -NoProfile -Command "Start-Process -FilePath '%HELPER%' -Verb RunAs -Wait"

if not exist "%DST%\VNyanWeightStudio.dll" ( echo [ERROR] Install copy failed (DLL not in place). & goto :fail )
if not exist "%DST%\WeightStudio.vnobj" ( echo [ERROR] Install copy failed (.vnobj not in place). & goto :fail )

echo.
echo ============================================
echo  Done. Installed to:
echo    %DST%
echo  Files: VNyanWeightStudio.dll, WeightStudio.vnobj
echo.
echo  (Re)start VNyan, open the Plugins window, click "Weight Studio".
echo ============================================
pause
exit /b 0

:fail
echo.
echo *** Build/Install failed. ***
pause
exit /b 1
