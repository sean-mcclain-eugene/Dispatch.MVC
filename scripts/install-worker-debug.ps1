#Requires -Version 5.1
<#
.SYNOPSIS
  Publish Dispatch.Worker (Debug) and install/update the local debug Windows Service.

.DESCRIPTION
  Safe to run from a Visual Studio post-build event.

  1. Publishes to a staging folder (no admin).
  2. Compares a stamp of the new exe/dlls to what is already installed.
  3. If nothing changed and the service exists: exits 0 with no UAC.
  4. If the worker changed or the service is missing: one UAC prompt, then
     stop → copy → sc create/config → start.

  Do not call New-Service from a nested RunAs. This script elevates itself
  once (and the parent exits) so sc.exe / New-Service run in the admin process.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\scripts\install-worker-debug.ps1

.EXAMPLE
  .\scripts\install-worker-debug.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [string]$ServiceName = "DispatchWorkerDebug",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [string]$InstallDir = "",
    [string]$ConnectionString = "",
    [switch]$Uninstall,
    [switch]$Force,
    [switch]$NoStart
)

$ErrorActionPreference = "Stop"

function Test-IsAdmin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object Security.Principal.WindowsPrincipal($id)
    return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-OutputStamp {
    param([string]$Directory)
    if (-not (Test-Path $Directory)) { return "" }
    $names = @(
        "Dispatch.Worker.exe",
        "Dispatch.Worker.dll",
        "Dispatch.Core.dll"
    )
    $parts = foreach ($n in $names) {
        $p = Join-Path $Directory $n
        if (Test-Path $p) { (Get-FileHash -Path $p -Algorithm SHA256).Hash }
    }
    return ($parts -join "-")
}

function Read-Stamp {
    param([string]$Path)
    if (Test-Path $Path) { return (Get-Content -Path $Path -Raw).Trim() }
    return ""
}

function Invoke-Sc {
    param([Parameter(Mandatory, ValueFromRemainingArguments)][string[]]$ScArgs)
    $out = & sc.exe @ScArgs 2>&1 | Out-String
    return @{ Code = $LASTEXITCODE; Text = $out }
}

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if (-not $InstallDir) {
    $InstallDir = Join-Path $RepoRoot "artifacts\worker-debug"
}
$StageDir = Join-Path $RepoRoot "artifacts\worker-debug-stage"
$StampPath = Join-Path $InstallDir ".install-stamp"
$Project = Join-Path $RepoRoot "Dispatch.Worker\Dispatch.Worker.csproj"
$DbFile = Join-Path $RepoRoot "App_Data\dispatch.db"
$ExeName = "Dispatch.Worker.exe"

# --- uninstall (admin) ---
if ($Uninstall) {
    if (-not (Test-IsAdmin)) {
        Write-Host "Elevating (UAC) to remove $ServiceName..."
        $p = Start-Process -FilePath "powershell.exe" -Verb RunAs -Wait -PassThru -ArgumentList @(
            "-NoProfile", "-ExecutionPolicy", "Bypass",
            "-File", "`"$PSCommandPath`"",
            "-Uninstall", "-ServiceName", $ServiceName
        )
        exit $p.ExitCode
    }
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $svc) {
        Write-Host "Service $ServiceName is not installed."
        exit 0
    }
    Write-Host "Stopping and removing $ServiceName"
    $null = Invoke-Sc stop $ServiceName
    Start-Sleep -Seconds 1
    $null = Invoke-Sc delete $ServiceName
    Write-Host "Removed $ServiceName."
    exit 0
}

if (-not (Test-Path $Project)) { throw "Worker project not found: $Project" }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "dotnet SDK not on PATH. Install .NET 8 SDK."
}

# --- publish to staging (no admin, does not lock the running service exe) ---
Write-Host "Publishing $Configuration -> $StageDir"
New-Item -ItemType Directory -Force -Path $StageDir | Out-Null
& dotnet publish $Project -c $Configuration -o $StageDir --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

$stageExe = Join-Path $StageDir $ExeName
if (-not (Test-Path $stageExe)) { throw "Expected $stageExe after publish." }

$newStamp = Get-OutputStamp -Directory $StageDir
$oldStamp = Read-Stamp -Path $StampPath
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
$needsInstall = $Force -or -not $svc -or ($newStamp -ne $oldStamp) -or -not (Test-Path (Join-Path $InstallDir $ExeName))

if (-not $needsInstall) {
    Write-Host "Worker unchanged (stamp $newStamp). $ServiceName left as $($svc.Status). Skipping UAC."
    exit 0
}

# --- one UAC, then THIS process must be the admin one (parent exits) ---
if (-not (Test-IsAdmin)) {
    $reason = if (-not $svc) { "service not installed" } else { "worker binaries changed" }
    Write-Host "Updating debug worker ($reason). Elevating once..."
    $argList = @(
        "-NoProfile", "-ExecutionPolicy", "Bypass",
        "-File", "`"$PSCommandPath`"",
        "-ServiceName", $ServiceName,
        "-Configuration", $Configuration,
        "-InstallDir", "`"$InstallDir`""
    )
    if ($ConnectionString) { $argList += @("-ConnectionString", "`"$ConnectionString`"") }
    if ($Force) { $argList += "-Force" }
    if ($NoStart) { $argList += "-NoStart" }
    $p = Start-Process -FilePath "powershell.exe" -Verb RunAs -Wait -PassThru -ArgumentList $argList
    if ($null -eq $p) { throw "UAC elevation was cancelled." }
    exit $p.ExitCode
}

# --- admin: stop, copy staging over the service folder, create/config, start ---
if ($svc) {
    Write-Host "Stopping $ServiceName"
    $null = Invoke-Sc stop $ServiceName
    $deadline = (Get-Date).AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 400
        $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    } while ($svc -and $svc.Status -ne "Stopped" -and (Get-Date) -lt $deadline)
}

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
& robocopy $StageDir $InstallDir /E /NFL /NDL /NJH /NJS /nc /ns /np | Out-Null
$copyCode = $LASTEXITCODE
if ($copyCode -ge 8) { throw "robocopy failed with code $copyCode" }

$exe = Join-Path $InstallDir $ExeName
if (-not (Test-Path $exe)) { throw "Expected $exe after copy." }

if (-not $ConnectionString) {
    New-Item -ItemType Directory -Force -Path (Split-Path $DbFile) | Out-Null
    $ConnectionString = "Data Source=$DbFile;Cache=Shared"
}

$devSettings = @{
    ConnectionStrings = @{ DefaultConnection = $ConnectionString }
    Logging           = @{
        LogLevel = @{
            Default                         = "Debug"
            "Microsoft.Hosting.Lifetime"    = "Information"
            "Microsoft.EntityFrameworkCore" = "Warning"
        }
    }
}
$devPath = Join-Path $InstallDir "appsettings.Development.json"
$devSettings | ConvertTo-Json -Depth 6 | Set-Content -Path $devPath -Encoding UTF8

$binPath = '"{0}"' -f $exe
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $svc) {
    Write-Host "Creating service $ServiceName (Manual)"
    # sc.exe requires a space after '='. Quoted binPath handles spaces in the repo path.
    $created = Invoke-Sc create $ServiceName binPath= $binPath start= demand DisplayName= "Dispatch Worker (Debug)"
    if ($created.Code -ne 0) {
        Write-Host $created.Text
        throw "sc create failed ($($created.Code)). Run this script from an elevated prompt if UAC was skipped."
    }
    $null = Invoke-Sc description $ServiceName "Local debug worker. Shares App_Data sqlite with Dispatch.Web. Not the production DispatchWorker service."
}
else {
    $null = Invoke-Sc config $ServiceName binPath= $binPath start= demand
}

$svcKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
if (Test-Path $svcKey) {
    New-ItemProperty -Path $svcKey -Name Environment -PropertyType MultiString -Force -Value @(
        "DOTNET_ENVIRONMENT=Development",
        "ASPNETCORE_ENVIRONMENT=Development"
    ) | Out-Null
}

Set-Content -Path $StampPath -Value $newStamp -Encoding ASCII

if (-not $NoStart) {
    $started = Invoke-Sc start $ServiceName
    if ($started.Code -ne 0 -and $started.Code -ne 1056) {
        Write-Host $started.Text
        throw "sc start failed ($($started.Code))"
    }
    Start-Sleep -Seconds 1
}

$svc = Get-Service $ServiceName
$cim = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
$proc = $cim.ProcessId
Write-Host ""
Write-Host "Name:      $($svc.Name)"
Write-Host "Status:    $($svc.Status)"
Write-Host "StartType: $($svc.StartType)"
Write-Host "Exe:       $exe"
Write-Host "Stamp:     $newStamp"
if ($proc -and $proc -ne 0) {
    Write-Host "PID:       $proc"
    Write-Host ""
    Write-Host "Attach the debugger:"
    Write-Host "  Visual Studio  Debug > Attach to Process > Dispatch.Worker.exe  (PID $proc)"
}
Write-Host ""
Write-Host "Logs: Event Viewer > Windows Logs > Application"
Write-Host "Stop: Stop-Service $ServiceName"
Write-Host "Remove: powershell -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Uninstall"
