<#
.SYNOPSIS
    Builds Pulse from source and starts it. Every argument is passed on to tpulse.

.EXAMPLE
    ./run.ps1 -cpu -memory -history 5m
#>

$ErrorActionPreference = 'Stop'
$configuration = 'Release'

dotnet build "$PSScriptRoot/src/Pulse/Pulse.csproj" -c $configuration --nologo -v quiet
if ($LASTEXITCODE) { exit $LASTEXITCODE }

& "$PSScriptRoot/src/Pulse/bin/$configuration/net10.0-windows/tpulse.exe" @args
