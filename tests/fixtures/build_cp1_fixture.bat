@echo off
setlocal
set ROOT=%~dp0..\..
set MANAGED=%1
if "%MANAGED%"=="" set MANAGED=%ROOT%\game\Cuphead_Data\Managed
if not exist "%MANAGED%\UnityEngine.CoreModule.dll" (
  echo Missing UnityEngine.CoreModule.dll in %MANAGED%
  exit /b 1
)
set OUT=%ROOT%\work\restart-20261031\cp1-final-narrow-builder\fixture
if not exist "%OUT%" mkdir "%OUT%"
"C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe" /nologo /target:library ^
  /out:"%OUT%\CP1CoroutineFixture.dll" ^
  /r:"%MANAGED%\UnityEngine.CoreModule.dll" ^
  "%ROOT%\tests\fixtures\CP1CoroutineFixture.cs"
if errorlevel 1 exit /b 1
python "%ROOT%\tools\build_metadata.py" ^
  "%OUT%\CP1CoroutineFixture.dll" "%OUT%\CP1CoroutineFixture.chm"
exit /b %errorlevel%
