@echo off
setlocal EnableDelayedExpansion
REM Rebuilds Weight Studio from source: compiles the DLL with Unity's Roslyn compiler, then
REM builds the .vnobj AssetBundle via Unity batchmode, then stages dist\.
REM Run install.bat afterwards to put the result into VNyan.
REM
REM Usage:  build_vnobj.bat ["<your VNyan folder>"]
REM The VNyan folder is the one with VNyan.exe inside it (the DLL is compiled against the
REM files in its VNyan_Data\Managed folder). Without an argument the script uses the VNYAN
REM environment variable, else looks for VNyan the same way install.ps1 does, else asks.
REM Set UNITY to a Unity.exe path to use an editor other than 2022.3.22f1 from Unity Hub.

set "PROJ=%~dp0"
set "VNYANDIR=%~1"
if not defined VNYANDIR if defined VNYAN set "VNYANDIR=%VNYAN%"
if not defined VNYANDIR if exist "%PROJ%install.ps1" (
    for /f "usebackq delims=" %%P in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%PROJ%install.ps1" -ListFound`) do (
        if not defined VNYANDIR if exist "%%P\VNyan.exe" set "VNYANDIR=%%P"
    )
)
if defined VNYANDIR goto :havevnyan
echo Where is your VNyan folder? It is the one with VNyan.exe inside it.
set /p "VNYANDIR=Paste the full path and press Enter: "
if not defined VNYANDIR ( echo ERROR: no VNyan folder given. & goto :fail )
:havevnyan
set "VNYANDIR=%VNYANDIR:"=%"
if not exist "%VNYANDIR%\VNyan.exe" ( echo ERROR: there is no VNyan.exe in "%VNYANDIR%". & goto :fail )

set "MGD=%VNYANDIR%\VNyan_Data\Managed"
set "VNI=%MGD%\VNyanInterface.dll"
set "UBUILD=%PROJ%_unitybuild"
set "PLUGINS=%UBUILD%\Assets\Plugins"
set "DLL=%PLUGINS%\VNyanWeightStudio.dll"
if not defined UNITY set "UNITY=C:\Program Files\Unity\Hub\Editor\2022.3.22f1\Editor\Unity.exe"

echo VNyan folder: "%VNYANDIR%"
if not exist "%MGD%\UnityEngine.dll" ( echo ERROR: VNyan managed DLLs not found at "%MGD%". & goto :fail )
if not exist "%VNI%" ( echo ERROR: VNyanInterface.dll not found at "%VNI%". & goto :fail )
if not exist "%UNITY%" ( echo ERROR: Unity 2022.3.22f1 not found. Install it via Unity Hub, or set UNITY to your Unity.exe. & goto :fail )

REM --- locate a C# compiler that ships with Unity: Editor\Data\Tools\Roslyn\csc.exe (Unity 2019.4
REM to 2020.3, any editor in Unity Hub), else the csc.dll that Unity 2021.3 and later (including the
REM 2022.3.22f1 above) run with their own dotnet.exe ---
set "CSC="
set "CSCDLL="
for /d %%E in ("C:\Program Files\Unity\Hub\Editor\*") do (
    if exist "%%E\Editor\Data\Tools\Roslyn\csc.exe" if not defined CSC set "CSC=%%E\Editor\Data\Tools\Roslyn\csc.exe"
)
if not defined CSC for %%U in ("%UNITY%") do (
    if exist "%%~dpUData\DotNetSdkRoslyn\csc.dll" if exist "%%~dpUData\NetCoreRuntime\dotnet.exe" (
        set "CSC=%%~dpUData\NetCoreRuntime\dotnet.exe"
        set "CSCDLL="%%~dpUData\DotNetSdkRoslyn\csc.dll""
    )
)
if not defined CSC ( echo ERROR: No C# compiler found in your Unity editors. & goto :fail )

echo [1/3] Compiling VNyanWeightStudio.dll ...
if not exist "%PLUGINS%" mkdir "%PLUGINS%"
if exist "%DLL%" del /q "%DLL%"
"!CSC!" !CSCDLL! -target:library -nostdlib+ -noconfig -optimize+ -out:"%DLL%" ^
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
 -reference:"%MGD%\UnityEngine.ScreenCaptureModule.dll" ^
 -reference:"%MGD%\UnityEngine.UnityWebRequestModule.dll" ^
 -reference:"%MGD%\Newtonsoft.Json.dll" ^
 -reference:"%VNI%" ^
 "%PROJ%WeightModel.cs" "%PROJ%WeightTools.cs" "%PROJ%VbxFormat.cs" ^
 "%PROJ%VbxExporter.cs" "%PROJ%GltfVrmExporter.cs" ^
 "%PROJ%WeightHeatmap.cs" "%PROJ%WeightTransfer.cs" ^
 "%PROJ%ColliderGen.cs" "%PROJ%CollidersExport.cs" "%PROJ%ColliderEditorPanel.cs" "%PROJ%ColliderImport.cs" "%PROJ%UnityPackageExport.cs" ^
 "%PROJ%BoneWeightStudioPlugin.cs" "%PROJ%WindowDrag.cs" "%PROJ%NativeFileDialog.cs" ^
 "%PROJ%VsfAvatarReader.cs" ^
 "%PROJ%SectionsPanel.cs" "%PROJ%MirrorPanel.cs" "%PROJ%WeightMirror.cs" "%PROJ%PluginConflicts.cs" ^
 "%PROJ%DebugProbe.cs" "%PROJ%LiveShapeKeys.cs" "%PROJ%PluginChrome.cs"
if not exist "%DLL%" ( echo ERROR: DLL compile failed. & goto :fail )

echo [2/3] Building .vnobj via Unity batchmode (this can take a few minutes) ...
"%UNITY%" -batchmode -quit -projectPath "%UBUILD%" -executeMethod WeightStudioBuild.Build -logFile "%UBUILD%\build.log"
set "VNOBJ=%UBUILD%\AssetBundles\WeightStudio.vnobj"
if not exist "%VNOBJ%" ( echo ERROR: .vnobj build failed. See "%UBUILD%\build.log". & goto :fail )

echo [3/3] Staging dist\ ...
if not exist "%PROJ%dist" mkdir "%PROJ%dist"
copy /Y "%DLL%"   "%PROJ%dist\" >nul
copy /Y "%VNOBJ%" "%PROJ%dist\" >nul

echo.
echo Build complete. dist\ contains:
echo   VNyanWeightStudio.dll
echo   WeightStudio.vnobj
echo Run install.bat to install it into VNyan.
pause
exit /b 0

:fail
echo.
echo *** Build failed. ***
pause
exit /b 1
