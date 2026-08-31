#Requires -Version 5.1
<#
.SYNOPSIS
  Build Dispatch.Worker (Debug) and install it as a local Windows Service.

.DESCRIPTION
  For debugging the IIS + worker split on your own machine. Re-run after code
  changes: it stops the service, publishes, and starts it again.

  - Self-elevates (UAC) if you are not already Administrator.
  - Publishes Debug with symbols so you can Attach to Process in Visual Studio.
  - Sets DOTNET_ENVIRONMENT=Development.
  - Points the service at the repo App_Data\dispatch.db so `dotnet run` of
    Dispatch.Web and this service share one sqlite file.
  - Startup type Manual (does not come back at boot). Use install-worker.ps1
    for the production Automatic service.

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
    [switch]$NoStart
)

$ErrorActionPreference = "Stop"

function Test-IsAdmin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object Security.Principal.WindowsPrincipal($id)
    return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-IsAdmin)) {
    $argList = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $PSCommandPath)
    foreach ($key in $PSBoundParameters.Keys) {
        $val = $PSBoundParameters[$key]
        if ($val -is [switch]) {
            if ($val.IsPresent) { $argList += "-$key" }
        }
        else {
            $argList += "-$key"
            $argList += "$val"
        }
    }
    Write-Host "Elevating (UAC) to install the Windows Service..."
    $p = Start-Process -FilePath "powershell.exe" -Verb RunAs -ArgumentList $argList -Wait -PassThru 
    #exit $p.ExitCode
}

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if (-not $InstallDir) {
    $InstallDir = Join-Path $RepoRoot "artifacts\worker-debug"
}
$Project = Join-Path $RepoRoot "Dispatch.Worker\Dispatch.Worker.csproj"
$DbFile = Join-Path $RepoRoot "App_Data\dispatch.db"

if ($Uninstall) {
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $svc) {
        Write-Host "Service $ServiceName is not installed."
        exit 0
    }
    Write-Host "Stopping and removing $ServiceName"
    Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 1
    Write-Host "Removed $ServiceName."
    exit 0
}

if (-not (Test-Path $Project)) {
    throw "Worker project not found: $Project"
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "dotnet SDK not on PATH. Install .NET 8 SDK."
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Stopping $ServiceName"
    Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Service $ServiceName).Status -ne "Stopped" -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 400
    }
}

Write-Host "Publishing $Configuration -> $InstallDir"
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
& dotnet publish $Project -c $Configuration -o $InstallDir --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

$exe = Join-Path $InstallDir "Dispatch.Worker.exe"
if (-not (Test-Path $exe)) { throw "Expected $exe after publish." }

if (-not $ConnectionString) {
    New-Item -ItemType Directory -Force -Path (Split-Path $DbFile) | Out-Null
    $ConnectionString = "Data Source=$DbFile;Cache=Shared"
}

$devSettings = @{
    ConnectionStrings = @{ DefaultConnection = $ConnectionString }
    Logging           = @{
        LogLevel = @{
            Default                      = "Debug"
            "Microsoft.Hosting.Lifetime" = "Information"
            "Microsoft.EntityFrameworkCore" = "Warning"
        }
    }
}
$devPath = Join-Path $InstallDir "appsettings.Development.json"
$devSettings | ConvertTo-Json -Depth 6 | Set-Content -Path $devPath -Encoding UTF8
Write-Host "Wrote $devPath"
Write-Host "  $ConnectionString"

if ($existing) {
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 1
}

Write-Host "Creating service $ServiceName (Manual, Development)"
Start-Process powershell.exe -Verb RunAs -ArgumentList '-Command "New-Service -Name ''Dispatch Worker (Debug)'' -BinaryPathName ''$exe''  -Description ''Local debug worker. Claims Pending rows from the repo sqlite/SQL. Manual start; not the production DispatchWorker service.'' -StartupType Manual | Out-Null"'

#New-Service -Name $ServiceName `
#    -BinaryPathName "`"$exe`"" `
#    -DisplayName "Dispatch Worker (Debug)" `
#    -Description "Local debug worker. Claims Pending rows from the repo sqlite/SQL. Manual start; not the production DispatchWorker service." `
#    -StartupType Manual | Out-Null


# .NET Worker reads these at process start. Registry MultiString = one VAR=value per line.
$svcKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
New-ItemProperty -Path $svcKey -Name Environment -PropertyType MultiString -Force -Value @(
    "DOTNET_ENVIRONMENT=Development",
    "ASPNETCORE_ENVIRONMENT=Development"
) | Out-Null

# Do not sc failure restart — a crashing debug build should stay down so you see it.

if (-not $NoStart) {
    Start-Service $ServiceName
    Start-Sleep -Seconds 1
}

$svc = Get-Service $ServiceName
$proc = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" | Select-Object -ExpandProperty ProcessId
Write-Host ""
Write-Host "Name:      $($svc.Name)"
Write-Host "Status:    $($svc.Status)"
Write-Host "StartType: $($svc.StartType)"
Write-Host "Exe:       $exe"
if ($proc -and $proc -ne 0) {
    Write-Host "PID:       $proc"
    Write-Host ""
    Write-Host "Attach the debugger:"
    Write-Host "  Visual Studio  Debug > Attach to Process > Dispatch.Worker.exe  (PID $proc)"
    Write-Host "  VS Code        Run and Debug > .NET Attach > $proc"
}
Write-Host ""
Write-Host "Logs: Event Viewer > Windows Logs > Application  (source DispatchWorker / .NET Runtime)"
Write-Host "Web:  dotnet run --project Dispatch.Web   (same sqlite: $DbFile)"
Write-Host "Stop: Stop-Service $ServiceName"
Write-Host "Remove: powershell -ExecutionPolicy Bypass -File .\scripts\install-worker-debug.ps1 -Uninstall"
