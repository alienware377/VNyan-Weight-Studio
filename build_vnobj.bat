@echo off
setlocal EnableDelayedExpansion
REM Rebuilds the Weight Studio SDK plugin: compiles the DLL with Unity's Roslyn
REM compiler, then builds the .vnobj AssetBundle via Unity batchmode, then stages dist\.

set "PROJ=%~dp0"
set "MGD=C:\Program Files\VNyan\VNyan_Data\Managed"
set "VNI=%MGD%\VNyanInterface.dll"
set "UBUILD=%PROJ%_unitybuild"
set "PLUGINS=%UBUILD%\Assets\Plugins"
set "DLL=%PLUGINS%\VNyanWeightStudio.dll"
set "UNITY=C:\Program Files\Unity\Hub\Editor\2022.3.22f1\Editor\Unity.exe"

if not exist "%MGD%\UnityEngine.dll" ( echo [!] VNyan managed DLLs not found at "%MGD%". & goto :fail )
if not exist "%VNI%" ( echo [!] VNyanInterface.dll not found at "%VNI%". & goto :fail )
if not exist "%UNITY%" ( echo [!] Unity 2022.3.22f1 not found. Install it via Unity Hub. & goto :fail )

REM --- locate Unity's Roslyn compiler (any editor) ---
set "CSC="
for /d %%E in ("C:\Program Files\Unity\Hub\Editor\*") do (
    if exist "%%E\Editor\Data\Tools\Roslyn\csc.exe" if not defined CSC set "CSC=%%E\Editor\Data\Tools\Roslyn\csc.exe"
)
if not defined CSC ( echo [!] No Unity Roslyn compiler found. & goto :fail )

echo [1/3] Compiling VNyanWeightStudio.dll ...
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
 "%PROJ%BoneWeightStudioPlugin.cs" "%PROJ%WindowDrag.cs" "%PROJ%NativeFileDialog.cs" ^
 "%PROJ%VsfAvatarReader.cs"
if not exist "%DLL%" ( echo [!] DLL compile failed. & goto :fail )

echo [2/3] Building .vnobj via Unity batchmode (this can take a few minutes) ...
"%UNITY%" -batchmode -quit -projectPath "%UBUILD%" -executeMethod WeightStudioBuild.Build -logFile "%UBUILD%\build.log"
set "VNOBJ=%UBUILD%\AssetBundles\WeightStudio.vnobj"
if not exist "%VNOBJ%" ( echo [!] .vnobj build failed. See "%UBUILD%\build.log". & goto :fail )

echo [3/3] Staging dist\ ...
if not exist "%PROJ%dist" mkdir "%PROJ%dist"
copy /Y "%DLL%"   "%PROJ%dist\" >nul
copy /Y "%VNOBJ%" "%PROJ%dist\" >nul

echo.
echo Build complete. dist\ contains:
echo   VNyanWeightStudio.dll
echo   WeightStudio.vnobj
echo Run install_sdk.bat to install into VNyan.
pause
exit /b 0

:fail
echo.
echo *** Build failed. ***
pause
exit /b 1
