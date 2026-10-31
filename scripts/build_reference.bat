@echo off
setlocal
set ROOT=%~dp0..
set MANAGED=%1
if "%MANAGED%"=="" set MANAGED=%ROOT%\game\Cuphead_Data\Managed
if not exist "%MANAGED%\Assembly-CSharp.dll" (
  echo Usage: scripts\build_reference.bat [managed-dir]
  exit /b 1
)
if not exist "%ROOT%\work\reference" mkdir "%ROOT%\work\reference"
copy /Y "%MANAGED%\*.dll" "%ROOT%\work\reference\" >nul
"C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe" /nologo /target:exe ^
  /out:"%ROOT%\work\reference\EaseReference.exe" ^
  /r:"%ROOT%\work\reference\Assembly-CSharp.dll" ^
  /r:"%ROOT%\work\reference\UnityEngine.CoreModule.dll" ^
  "%ROOT%\tests\reference\EaseReference.cs"
if errorlevel 1 exit /b 1
echo Built %ROOT%\work\reference\EaseReference.exe
