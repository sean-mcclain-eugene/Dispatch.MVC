#Requires -Version 5.1
<#
.SYNOPSIS
  Publish Dispatch.Worker (Debug) and install/update the local debug Windows Service.

.DESCRIPTION
  Writes a full trace to scripts\install-worker-debug.log (repo-relative).
  If the Visual Studio post-build exits 1, check that file in and we can read it.

  1. Restores + publishes to a staging folder as the current user (nuget.org).
  2. Compares a stamp of the new exe/dlls to what is already installed.
  3. If nothing changed and the service exists: exits 0 with no UAC.
  4. If the worker changed or the service is missing: one UAC prompt. The
     elevated process only copies staging and calls sc.exe — it does NOT
     restore NuGet as Administrator (that feed is often VS Offline only).
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
    [switch]$NoStart,
    [switch]$Apply
)

$ErrorActionPreference = "Stop"
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$script:LogFile = Join-Path $PSScriptRoot "install-worker-debug.last.txt"

function Write-Log {
    param(
        [Parameter(Mandatory, ValueFromPipeline)]
        [AllowEmptyString()]
        [string]$Message,
        [ValidateSet("INFO", "WARN", "ERROR")]
        [string]$Level = "INFO"
    )
    process {
        $line = "{0:yyyy-MM-dd HH:mm:ss}Z [{1}] {2}" -f (Get-Date).ToUniversalTime(), $Level, $Message
        Add-Content -LiteralPath $script:LogFile -Value $line -Encoding UTF8
        Write-Host $line
    }
}

function Invoke-Logged {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$ArgumentList
    )
    Write-Log "EXEC $FilePath $($ArgumentList -join ' ')"
    $output = & $FilePath @ArgumentList 2>&1
    $code = $LASTEXITCODE
    foreach ($row in $output) {
        Write-Log ("  " + ($row | Out-String).TrimEnd())
    }
    Write-Log "EXIT $code  ($FilePath)"
    return $code
}

try {
    if (-not $Apply -and -not $Uninstall) {
        Set-Content -LiteralPath $script:LogFile -Value "" -Encoding UTF8
    }

    Write-Log "==== start pid=$PID user=$env:USERNAME machine=$env:COMPUTERNAME ===="
    Write-Log "admin=$([bool]((New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))) Apply=$Apply Uninstall=$Uninstall Force=$Force"
    Write-Log "cwd=$((Get-Location).Path)"
    Write-Log "script=$PSCommandPath"
    Write-Log "repo=$RepoRoot"
    Write-Log "ps=$($PSVersionTable.PSVersion) 64bit=$([Environment]::Is64BitProcess)"
    Write-Log "bound=$($PSBoundParameters.Keys -join ',')"

    function Test-IsAdmin {
        $id = [Security.Principal.WindowsIdentity]::GetCurrent()
        $p = New-Object Security.Principal.WindowsPrincipal($id)
        return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    }

    function Resolve-Dotnet {
        $machine = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
        $userLocal = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
        $fromPath = Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
        Write-Log "dotnet candidates machine=$machine path=$fromPath user=$userLocal"
        foreach ($c in @($machine, $fromPath, $userLocal)) {
            if ($c -and (Test-Path $c)) { return $c }
        }
        throw "dotnet SDK not found. Install the .NET 10 SDK from https://dot.net (need Program Files\dotnet, not only a user PATH)."
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
        Write-Log "sc.exe $($ScArgs -join ' ')"
        $out = & sc.exe @ScArgs 2>&1 | Out-String
        Write-Log ("sc exit=$LASTEXITCODE text=" + $out.Trim())
        return @{ Code = $LASTEXITCODE; Text = $out }
    }

    function Invoke-Elevate {
        param([string[]]$ExtraArgs)
        Write-Log "Elevating (UAC) once... extra=$($ExtraArgs -join ' ')"
        $argList = @(
            "-NoProfile", "-ExecutionPolicy", "Bypass",
            "-File", "`"$PSCommandPath`""
        ) + $ExtraArgs
        $p = Start-Process -FilePath "powershell.exe" -Verb RunAs -Wait -PassThru -ArgumentList $argList
        if ($null -eq $p) { throw "UAC elevation was cancelled." }
        Write-Log "elevated child exit=$($p.ExitCode)"
        exit $p.ExitCode
    }

    Set-Location $RepoRoot
    if (-not $InstallDir) {
        $InstallDir = Join-Path $RepoRoot "artifacts\worker-debug"
    }
    $StageDir = Join-Path $RepoRoot "artifacts\worker-debug-stage"
    $StampPath = Join-Path $InstallDir ".install-stamp"
    $Project = Join-Path $RepoRoot "Dispatch.Worker\Dispatch.Worker.csproj"
    $DbFile = Join-Path $RepoRoot "App_Data\dispatch.db"
    $ExeName = "Dispatch.Worker.exe"
    $NugetOrg = "https://api.nuget.org/v3/index.json"
    Write-Log "InstallDir=$InstallDir StageDir=$StageDir Project=$Project"

    if ($Uninstall) {
        if (-not (Test-IsAdmin)) {
            Invoke-Elevate -ExtraArgs @("-Uninstall", "-ServiceName", $ServiceName)
        }
        $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if (-not $svc) {
            Write-Log "Service $ServiceName is not installed."
            exit 0
        }
        Write-Log "Stopping and removing $ServiceName"
        $null = Invoke-Sc stop $ServiceName
        Start-Sleep -Seconds 1
        $null = Invoke-Sc delete $ServiceName
        Write-Log "Removed $ServiceName."
        exit 0
    }

    if (-not (Test-Path $Project)) { throw "Worker project not found: $Project" }

    if (-not $Apply) {
        $dotnet = Resolve-Dotnet
        Write-Log "Using $dotnet"
        $null = Invoke-Logged -FilePath $dotnet -ArgumentList @("--list-sdks")
        $null = Invoke-Logged -FilePath $dotnet -ArgumentList @("--info")

        Write-Log "Restoring + publishing $Configuration -> $StageDir"
        New-Item -ItemType Directory -Force -Path $StageDir | Out-Null
        $restore = Invoke-Logged -FilePath $dotnet -ArgumentList @(
            "restore", $Project, "--nologo", "--force", "--ignore-failed-sources", "--source", $NugetOrg
        )
        if ($restore -ne 0) { throw "dotnet restore failed ($restore). Need nuget.org and the .NET 10 SDK." }
        $publish = Invoke-Logged -FilePath $dotnet -ArgumentList @(
            "publish", $Project, "--configuration", $Configuration, "--output", $StageDir, "--nologo", "--no-restore"
        )
        if ($publish -ne 0) { throw "dotnet publish failed ($publish)" }

        $stageExe = Join-Path $StageDir $ExeName
        if (-not (Test-Path $stageExe)) { throw "Expected $stageExe after publish." }

        $newStamp = Get-OutputStamp -Directory $StageDir
        $oldStamp = Read-Stamp -Path $StampPath
        $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        Write-Log "stamp new=$newStamp old=$oldStamp svc=$($svc.Status)"
        $needsInstall = $Force -or -not $svc -or ($newStamp -ne $oldStamp) -or -not (Test-Path (Join-Path $InstallDir $ExeName))
        Write-Log "needsInstall=$needsInstall"

        if (-not $needsInstall) {
            Write-Log "Worker unchanged. $ServiceName left as $($svc.Status). Skipping UAC."
            exit 0
        }

        if (-not (Test-IsAdmin)) {
            $reason = if (-not $svc) { "service not installed" } else { "worker binaries changed" }
            Write-Log "Updating debug worker ($reason)."
            $applyArgs = @(
                "-Apply",
                "-ServiceName", $ServiceName,
                "-Configuration", $Configuration,
                "-InstallDir", "`"$InstallDir`""
            )
            if ($ConnectionString) { $applyArgs += @("-ConnectionString", "`"$ConnectionString`"") }
            if ($Force) { $applyArgs += "-Force" }
            if ($NoStart) { $applyArgs += "-NoStart" }
            Invoke-Elevate -ExtraArgs $applyArgs
        }
    }

    if (-not (Test-IsAdmin)) {
        throw "Service install requires Administrator. Re-run and accept the UAC prompt."
    }

    $stageExe = Join-Path $StageDir $ExeName
    if (-not (Test-Path $stageExe)) { throw "Staging output missing: $stageExe. Publish as your user first (do not restore as Administrator)." }

    $newStamp = Get-OutputStamp -Directory $StageDir
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($svc) {
        Write-Log "Stopping $ServiceName"
        $null = Invoke-Sc stop $ServiceName
        $deadline = (Get-Date).AddSeconds(20)
        do {
            Start-Sleep -Milliseconds 400
            $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        } while ($svc -and $svc.Status -ne "Stopped" -and (Get-Date) -lt $deadline)
        Write-Log "stop wait status=$($svc.Status)"
    }

    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    Write-Log "robocopy $StageDir -> $InstallDir"
    & robocopy $StageDir $InstallDir /E /NFL /NDL /NJH /NJS /nc /ns /np | Out-Null
    $copyCode = $LASTEXITCODE
    Write-Log "robocopy exit=$copyCode (0-7 is success)"
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
    Write-Log "wrote $devPath"

    $binPath = '"{0}"' -f $exe
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $svc) {
        Write-Log "Creating service $ServiceName (Manual) binPath=$binPath"
        $created = Invoke-Sc create $ServiceName binPath= $binPath start= demand DisplayName= "Dispatch Worker (Debug)"
        if ($created.Code -ne 0) {
            throw "sc create failed ($($created.Code)). $($created.Text)"
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
        Write-Log "set $svcKey Environment=Development"
    }

    Set-Content -Path $StampPath -Value $newStamp -Encoding ASCII
    Write-Log "stamp written $newStamp"

    if (-not $NoStart) {
        $started = Invoke-Sc start $ServiceName
        if ($started.Code -ne 0 -and $started.Code -ne 1056) {
            throw "sc start failed ($($started.Code)). $($started.Text)"
        }
        Start-Sleep -Seconds 1
    }

    $svc = Get-Service $ServiceName
    $cim = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
    $proc = $cim.ProcessId
    Write-Log "DONE Name=$($svc.Name) Status=$($svc.Status) StartType=$($svc.StartType) Exe=$exe PID=$proc"
    Write-Log "log file: $script:LogFile"
    exit 0
}
catch {
    $err = $_ | Out-String
    $stack = $_.ScriptStackTrace
    try { Write-Log "FAILED $err" -Level ERROR } catch {}
    try { Write-Log "STACK $stack" -Level ERROR } catch {}
    Write-Host "FAILED. See $script:LogFile (check this file in for diagnosis)."
    exit 1
}
