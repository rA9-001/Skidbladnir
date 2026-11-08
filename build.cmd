@echo off
rem Builds Compresser.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (echo C# compiler not found. & exit /b 1)

"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu /out:"%~dp0Compresser.exe" ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll "%~dp0Compresser.cs"
if errorlevel 1 exit /b 1
echo Built %~dp0Compresser.exe
