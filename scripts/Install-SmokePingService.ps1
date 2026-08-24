<#
.SYNOPSIS
    Installs SmokePing.NET as a Windows service.

.DESCRIPTION
    Publishes the application to the given directory and registers it with the
    Service Control Manager using sc.exe. The service is configured to restart
    automatically if it stops unexpectedly, which matters for a monitoring tool:
    a gap in the graphs is indistinguishable from an outage.

    Run this from an elevated PowerShell prompt.

.PARAMETER InstallPath
    Where to publish the application. Defaults to C:\Program Files\SmokePing.NET.

.PARAMETER ServiceName
    The Windows service name. Defaults to SmokePingNet.

.PARAMETER ConfigPath
    Configuration file the service should use. Defaults to smokeping.json in the
    config folder below InstallPath.

.EXAMPLE
    .\Install-SmokePingService.ps1
    .\Install-SmokePingService.ps1 -InstallPath D:\SmokePing -ServiceName SmokePing
#>

[CmdletBinding()]
param(
    [string] $InstallPath = "$env:ProgramFiles\SmokePing.NET",
    [string] $ServiceName = 'SmokePingNet',
    [string] $ConfigPath
)

$ErrorActionPreference = 'Stop'

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This script must be run from an elevated PowerShell prompt.'
    }
}

Assert-Administrator

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot 'src\SmokePing.Net\SmokePing.Net.csproj'

if (-not (Test-Path $project)) {
    throw "Could not find $project. Run this script from the repository."
}

if (-not $ConfigPath) {
    $ConfigPath = Join-Path $InstallPath 'config\smokeping.json'
}

Write-Host "Publishing to $InstallPath ..."
dotnet publish $project --configuration Release --output $InstallPath
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

# Keep an existing configuration; only seed one on a fresh install.
$configDirectory = Split-Path -Parent $ConfigPath
New-Item -ItemType Directory -Force -Path $configDirectory | Out-Null
if (-not (Test-Path $ConfigPath)) {
    Copy-Item (Join-Path $repositoryRoot 'config\smokeping.json') $ConfigPath
    Write-Host "Seeded configuration at $ConfigPath - edit it before relying on the results."
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Removing the existing $ServiceName service ..."
    if ($existing.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
    }
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

$exe = Join-Path $InstallPath 'SmokePing.Net.exe'
# --service makes the process talk to the service control manager.
$binaryPath = '"{0}" --service --service-name {1} --config "{2}"' -f $exe, $ServiceName, $ConfigPath

Write-Host "Registering the $ServiceName service ..."
sc.exe create $ServiceName binPath= $binaryPath start= auto DisplayName= 'SmokePing.NET' | Out-Null
sc.exe description $ServiceName 'Latency measurement and smoke graphs.' | Out-Null

# Restart on failure: after 5s, then 30s, then every 60s, resetting the count daily.
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/30000/restart/60000 | Out-Null

Start-Service -Name $ServiceName
Write-Host ''
Write-Host "$ServiceName is running. The web interface is on the address in general.listenUrl."
Write-Host "Logs: $(Join-Path (Split-Path -Parent $ConfigPath) 'logs\smokeping.log')"
Write-Host "Stop with: Stop-Service $ServiceName"
