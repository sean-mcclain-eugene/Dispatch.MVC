# Run from an elevated PowerShell prompt, from the repo root.
# Publishes Dispatch.Worker and registers it as a Windows Service.
param(
    [string]$InstallDir = "C:\Services\Dispatch.Worker",
    [string]$ServiceName = "DispatchWorker"
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "..\Dispatch.Worker\Dispatch.Worker.csproj"

Write-Host "Publishing worker to $InstallDir"
dotnet publish $project -c Release -o $InstallDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$exe = Join-Path $InstallDir "Dispatch.Worker.exe"
if (-not (Test-Path $exe)) { throw "Expected $exe after publish." }

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Stopping existing $ServiceName"
    Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 1
}

Write-Host "Creating service $ServiceName"
New-Service -Name $ServiceName `
    -BinaryPathName $exe `
    -DisplayName "Dispatch Worker" `
    -Description "Runs Dispatch long jobs outside IIS. Claims Pending rows from the shared database." `
    -StartupType Automatic

# Restart on crash
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null

Start-Service $ServiceName
Get-Service $ServiceName | Format-List Name, Status, StartType
Write-Host "Point Dispatch.Web and Dispatch.Worker at the SAME connection string (SQL Server, or a shared sqlite file)."
