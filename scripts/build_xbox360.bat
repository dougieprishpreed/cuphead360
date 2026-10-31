@echo off
setlocal
set ROOT=%~dp0..
set CONFIG=%1
if "%CONFIG%"=="" set CONFIG=Release

if not defined XEDK if exist "C:\Program Files (x86)\Microsoft Xbox 360 SDK" set XEDK=C:\Program Files (x86)\Microsoft Xbox 360 SDK
if not defined XEDK (
  echo XEDK is not set. Install the Xbox 360 XDK.
  exit /b 1
)

rem Make the XDK PowerPC compiler authoritative. Do not call vsvars32 here.
set PATH=%XEDK%\bin\win32;%WINDIR%\Microsoft.NET\Framework\v4.0.30319;%PATH%
set INCLUDE=%XEDK%\include\win32;%XEDK%\include\xbox;%XEDK%\include\xbox\sys;%INCLUDE%
set LIB=%XEDK%\lib\win32;%XEDK%\lib\xbox;%LIB%

if not exist "%ROOT%\build\logs" mkdir "%ROOT%\build\logs"
set MSBUILD=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe
echo Building Cuphead360 Xbox 360 %CONFIG%...
"%MSBUILD%" "%ROOT%\projects\xbox360\Cuphead360.vcxproj" /nologo /t:Rebuild /p:Configuration=%CONFIG% "/p:Platform=Xbox 360" /v:minimal /fl "/flp:logfile=%ROOT%\build\logs\xbox360_%CONFIG%.log;verbosity:normal"
set RESULT=%ERRORLEVEL%
if not "%RESULT%"=="0" (
  echo Xbox 360 build FAILED. See build\logs\xbox360_%CONFIG%.log
  exit /b %RESULT%
)
echo Xbox 360 build OK: %ROOT%\build\xbox360\default.xex
exit /b 0
