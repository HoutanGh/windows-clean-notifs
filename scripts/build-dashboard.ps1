Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

trap {
    Write-Host ""
    Write-Host "Build failed: $($_.Exception.Message)"
    exit 1
}

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$FrontendDir = Join-Path $RepoRoot "src\NotificationDashboard.Web"
$ProjectPath = Join-Path $RepoRoot "src\NotificationInspector\NotificationInspector.csproj"
$TestProjectPath = Join-Path $RepoRoot "tests\NotificationInspector.Tests\NotificationInspector.Tests.csproj"
$PublishDir = Join-Path $RepoRoot "artifacts\notification-inspector-dashboard"

function Resolve-Tool {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name,

        [string] $FallbackPath
    )

    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    if ($FallbackPath -and (Test-Path -LiteralPath $FallbackPath)) {
        return $FallbackPath
    }

    throw "Required tool '$Name' was not found. Install it, then rerun this script."
}

function Invoke-External {
    param(
        [Parameter(Mandatory = $true)]
        [string] $FilePath,

        [Parameter(Mandatory = $true)]
        [string[]] $Arguments,

        [Parameter(Mandatory = $true)]
        [string] $WorkingDirectory
    )

    $command = @((ConvertTo-CmdArgument $FilePath)) + ($Arguments | ForEach-Object { ConvertTo-CmdArgument $_ })
    $commandLine = "set `"PATH=$env:PATH`" && pushd $(ConvertTo-CmdArgument $WorkingDirectory) && $($command -join ' ') & set COMMAND_EXIT=%ERRORLEVEL% & popd & exit /b %COMMAND_EXIT%"

    & $env:ComSpec /d /s /c $commandLine
    $exitCode = $LASTEXITCODE

    if ($exitCode -ne 0) {
        throw "$FilePath $($Arguments -join ' ') failed with exit code $exitCode."
    }
}

function Invoke-WslCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Distro,

        [Parameter(Mandatory = $true)]
        [string] $Command
    )

    $wsl = Resolve-Tool -Name "wsl.exe"
    $argumentLine = "wsl.exe -d $Distro -- bash -lc $(ConvertTo-CmdArgument $Command)"
    & $env:ComSpec /d /c $argumentLine
    $exitCode = $LASTEXITCODE

    if ($exitCode -ne 0) {
        throw "wsl.exe -d $Distro -- bash -lc $Command failed with exit code $exitCode."
    }
}

function Invoke-FrontendNpm {
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $Arguments
    )

    $frontendWslPath = Get-WslPathInfo -Path $FrontendDir
    if ($null -ne $frontendWslPath) {
        $command = "cd $(ConvertTo-BashArgument $frontendWslPath.LinuxPath) && npm $($Arguments | ForEach-Object { ConvertTo-BashArgument $_ })"
        Invoke-WslCommand -Distro $frontendWslPath.Distro -Command $command
        return
    }

    $npm = Resolve-Tool -Name "npm.cmd"
    Invoke-External -FilePath $npm -Arguments $Arguments -WorkingDirectory $FrontendDir
}

function ConvertTo-CmdArgument {
    param([Parameter(Mandatory = $true)][string] $Value)

    return '"' + ($Value -replace '"', '\"') + '"'
}

function ConvertTo-BashArgument {
    param([Parameter(Mandatory = $true)][string] $Value)

    return "'" + $Value.Replace("'", "'\''") + "'"
}

function Get-WslPathInfo {
    param([Parameter(Mandatory = $true)][string] $Path)

    if ($Path -notmatch '^\\\\wsl(?:\.localhost|\$)\\([^\\]+)\\(.+)$') {
        return $null
    }

    [PSCustomObject]@{
        Distro = $Matches[1]
        LinuxPath = "/" + ($Matches[2] -replace '\\', '/')
    }
}

function Remove-DirectoryIfPresent {
    param([Parameter(Mandatory = $true)][string] $Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    Write-Step "Removing generated directory $Path"
    if (Remove-WslDirectoryIfPossible -Path $Path) {
        return
    }

    $lastError = $null
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            Remove-Item -LiteralPath $Path -Recurse -Force
            if (-not (Test-Path -LiteralPath $Path)) {
                return
            }
        }
        catch {
            $lastError = $_
        }

        Start-Sleep -Milliseconds 250
    }

    if ($null -ne $lastError) {
        throw $lastError
    }

    throw "Could not remove generated directory: $Path"
}

function Remove-WslDirectoryIfPossible {
    param([Parameter(Mandatory = $true)][string] $Path)

    $wslPath = Get-WslPathInfo -Path $Path
    if ($null -eq $wslPath) {
        return $false
    }

    $wsl = Get-Command "wsl.exe" -ErrorAction SilentlyContinue
    if ($null -eq $wsl) {
        return $false
    }

    $process = Start-Process `
        -FilePath $wsl.Source `
        -ArgumentList @("-d", $wslPath.Distro, "--", "rm", "-rf", "--", $wslPath.LinuxPath) `
        -NoNewWindow `
        -Wait `
        -PassThru

    if ($process.ExitCode -ne 0) {
        throw "wsl.exe could not remove generated directory $Path. Exit code: $($process.ExitCode)."
    }

    if (Test-Path -LiteralPath $Path) {
        throw "Generated directory still exists after WSL removal: $Path"
    }

    return $true
}

function Write-Step {
    param([Parameter(Mandatory = $true)][string] $Message)

    Write-Host ""
    Write-Host "==> $Message"
}

function Assert-NotificationInspectorNotRunning {
    $running = Get-Process -Name "NotificationInspector" -ErrorAction SilentlyContinue
    if ($null -eq $running) {
        return
    }

    $pids = ($running | ForEach-Object { $_.Id }) -join ", "
    throw "NotificationInspector.exe is currently running (PID: $pids). Stop it with Ctrl+C before rebuilding."
}

$DotNet = Resolve-Tool -Name "dotnet.exe" -FallbackPath "C:\Program Files\dotnet\dotnet.exe"

Write-Host "Repository: $RepoRoot"
Write-Host "Publish output: $PublishDir"

Remove-DirectoryIfPresent -Path (Join-Path $FrontendDir "node_modules")

Write-Step "Installing frontend dependencies"
Invoke-FrontendNpm -Arguments @("ci")

Write-Step "Running frontend tests"
Invoke-FrontendNpm -Arguments @("test")

Write-Step "Building frontend"
Invoke-FrontendNpm -Arguments @("run", "build")

Write-Step "Running .NET tests"
Invoke-External -FilePath $DotNet -Arguments @("run", "--project", $TestProjectPath) -WorkingDirectory $RepoRoot

Assert-NotificationInspectorNotRunning

Write-Step "Publishing Windows executable"
Invoke-External -FilePath $DotNet -Arguments @(
    "publish",
    $ProjectPath,
    "--configuration",
    "Debug",
    "--runtime",
    "win-x64",
    "--self-contained",
    "true",
    "--output",
    $PublishDir
) -WorkingDirectory $RepoRoot

Write-Step "Verifying publish output"
$ExePath = Join-Path $PublishDir "NotificationInspector.exe"
$IndexPath = Join-Path $PublishDir "wwwroot\index.html"
$AssetsDir = Join-Path $PublishDir "wwwroot\assets"

if (-not (Test-Path -LiteralPath $ExePath)) {
    throw "Published executable is missing: $ExePath"
}

if (-not (Test-Path -LiteralPath $IndexPath)) {
    throw "Compiled frontend index.html is missing: $IndexPath"
}

if (-not (Test-Path -LiteralPath $AssetsDir)) {
    throw "Compiled frontend assets directory is missing: $AssetsDir"
}

$CompiledAssets = Get-ChildItem -LiteralPath $AssetsDir -File -Recurse |
    Where-Object { $_.Extension -in @(".js", ".css") }
if ($CompiledAssets.Count -eq 0) {
    throw "Compiled frontend assets are missing from: $AssetsDir"
}

Write-Host ""
Write-Host "Build complete."
Write-Host "Run:"
Write-Host "  & `"$ExePath`" --serve --open-browser"
