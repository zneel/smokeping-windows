<#
.SYNOPSIS
    Stops and removes the SmokePing.NET Windows service.

.DESCRIPTION
    Removes the service registration. Measurement data and configuration are left
    in place; delete the install directory by hand if you want them gone too.

.PARAMETER ServiceName
    The Windows service name. Defaults to SmokePingNet.
#>

[CmdletBinding()]
param(
    [string] $ServiceName = 'SmokePingNet'
)

$ErrorActionPreference = 'Stop'

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $service) {
    Write-Host "No service named $ServiceName is installed."
    return
}

if ($service.Status -ne 'Stopped') {
    Stop-Service -Name $ServiceName -Force
}

sc.exe delete $ServiceName | Out-Null
Write-Host "$ServiceName removed. Measurement data and configuration were left untouched."
