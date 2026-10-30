@echo off
rem Rebuilds CcdParkMonitor.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /optimize+ /platform:x64 /target:winexe /out:CcdParkMonitor.exe CcdParkMonitor.cs
