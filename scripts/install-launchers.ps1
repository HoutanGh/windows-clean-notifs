Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$UserProfile = [Environment]::GetFolderPath("UserProfile")
$NotifsLauncher = Join-Path $UserProfile "notifs.cmd"
$BuildLauncher = Join-Path $UserProfile "build-notifs.cmd"

$NotifsCmd = @"
@echo off
setlocal
set "REPO_ROOT=$RepoRoot"
set "APP_EXE=%REPO_ROOT%\artifacts\notification-inspector-dashboard\NotificationInspector.exe"

if not exist "%APP_EXE%" (
  echo Published executable was not found:
  echo   "%APP_EXE%"
  echo.
  echo Run build-notifs.cmd first, then run notifs.cmd again.
  exit /b 1
)

"%APP_EXE%" --serve --open-browser %*
exit /b %ERRORLEVEL%
"@

$BuildCmd = @"
@echo off
setlocal
set "REPO_ROOT=$RepoRoot"
set "BUILD_SCRIPT=%REPO_ROOT%\scripts\build-dashboard.ps1"

if not exist "%BUILD_SCRIPT%" (
  echo Build script was not found:
  echo   "%BUILD_SCRIPT%"
  echo.
  echo Re-run scripts\install-launchers.ps1 from the repository.
  exit /b 1
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%BUILD_SCRIPT%" %*
exit /b %ERRORLEVEL%
"@

Set-Content -LiteralPath $NotifsLauncher -Value $NotifsCmd -Encoding ASCII
Set-Content -LiteralPath $BuildLauncher -Value $BuildCmd -Encoding ASCII

Write-Host "Launcher install complete."
Write-Host "Repository: $RepoRoot"
Write-Host "Created:"
Write-Host "  $NotifsLauncher"
Write-Host "  $BuildLauncher"
Write-Host ""
Write-Host "Daily use from Windows PowerShell:"
Write-Host "  .\notifs.cmd"
Write-Host ""
Write-Host "After code changes:"
Write-Host "  .\build-notifs.cmd"
Write-Host "  .\notifs.cmd"
Write-Host ""
Write-Host "No PATH or PowerShell profile changes were made."
