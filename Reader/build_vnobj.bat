@echo off
setlocal EnableDelayedExpansion
REM Rebuilds the Weight Studio Reader SDK plugin: compiles the DLL with Unity's
REM Roslyn compiler, then builds the .vnobj AssetBundle via Unity batchmode, then
REM stages dist\.

set "PROJ=%~dp0"
set "MGD=C:\Program Files\VNyan\VNyan_Data\Managed"
set "VNI=%MGD%\VNyanInterface.dll"
set "UBUILD=%PROJ%_unitybuild"
set "PLUGINS=%UBUILD%\Assets\Plugins"
set "DLL=%PLUGINS%\VNyanWeightStudioReader.dll"
set "UNITY=C:\Program Files\Unity\Hub\Editor\2022.3.22f1\Editor\Unity.exe"

if not exist "%MGD%\UnityEngine.dll" ( echo [!] VNyan managed DLLs not found at "%MGD%". & goto :fail )
if not exist "%VNI%" ( echo [!] VNyanInterface.dll not found at "%VNI%". & goto :fail )
if not exist "%UNITY%" ( echo [!] Unity 2022.3.22f1 not found. Install it via Unity Hub. & goto :fail )

set "CSC="
for /d %%E in ("C:\Program Files\Unity\Hub\Editor\*") do (
    if exist "%%E\Editor\Data\Tools\Roslyn\csc.exe" if not defined CSC set "CSC=%%E\Editor\Data\Tools\Roslyn\csc.exe"
)
if not defined CSC ( echo [!] No Unity Roslyn compiler found. & goto :fail )

echo [1/3] Compiling VNyanWeightStudioReader.dll ...
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
 -reference:"%MGD%\UnityEngine.AssetBundleModule.dll" ^
 -reference:"%MGD%\Unity.Collections.dll" ^
 -reference:"%MGD%\UnityEngine.SharedInternalsModule.dll" ^
 -reference:"%MGD%\UnityEngine.UIModule.dll" ^
 -reference:"%MGD%\UnityEngine.UI.dll" ^
 -reference:"%MGD%\UnityEngine.TextRenderingModule.dll" ^
 -reference:"%VNI%" ^
 "%PROJ%VbxFormat.cs" "%PROJ%VbxImporter.cs" ^
 "%PROJ%WeightStudioReaderPlugin.cs" "%PROJ%WindowDrag.cs"
if not exist "%DLL%" ( echo [!] DLL compile failed. & goto :fail )

echo [2/3] Building .vnobj via Unity batchmode (this can take a few minutes) ...
"%UNITY%" -batchmode -quit -projectPath "%UBUILD%" -executeMethod WeightStudioReaderBuild.Build -logFile "%UBUILD%\build.log"
set "VNOBJ=%UBUILD%\AssetBundles\WeightStudioReader.vnobj"
if not exist "%VNOBJ%" ( echo [!] .vnobj build failed. See "%UBUILD%\build.log". & goto :fail )

echo [3/3] Staging dist\ ...
if not exist "%PROJ%dist" mkdir "%PROJ%dist"
copy /Y "%DLL%"   "%PROJ%dist\" >nul
copy /Y "%VNOBJ%" "%PROJ%dist\" >nul

echo.
echo Build complete. dist\ contains:
echo   VNyanWeightStudioReader.dll
echo   WeightStudioReader.vnobj
echo Run install_sdk.bat to install into VNyan.
pause
exit /b 0

:fail
echo.
echo *** Build failed. ***
pause
exit /b 1
