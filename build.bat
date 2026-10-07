@echo off
rem Builds GamePowerPlan.exe using the C# compiler that comes with Windows.
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /out:GamePowerPlan.exe /win32manifest:GamePowerPlan.manifest /r:System.Windows.Forms.dll /r:System.Drawing.dll GamePowerPlan.cs
if errorlevel 1 (
  echo.
  echo Build failed.
) else (
  echo.
  echo Built GamePowerPlan.exe
)
pause
