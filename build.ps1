<#
.SYNOPSIS
    Builds Pulse, runs the unit tests and optionally publishes tpulse.exe.

.EXAMPLE
    ./build.ps1
    Release build plus unit tests.

.EXAMPLE
    ./build.ps1 -Publish
    Also publishes a single-file tpulse.exe to artifacts\win-x64.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [switch] $SkipTests,

    [switch] $Publish,

    [ValidateSet('win-x64', 'win-arm64')]
    [string] $Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet build Pulse.slnx -c $Configuration --nologo
    if ($LASTEXITCODE) { throw 'Build failed.' }

    if (-not $SkipTests) {
        dotnet test --solution Pulse.slnx -c $Configuration --no-build
        if ($LASTEXITCODE) { throw 'Tests failed.' }
    }

    if ($Publish) {
        $output = Join-Path $PSScriptRoot "artifacts\$Runtime"
        if (Test-Path $output) {
            Remove-Item $output -Recurse -Force
        }

        # Framework-dependent: the properties are passed directly, because some SDK versions drop the
        # --self-contained switch and then publish the whole runtime (120 MB instead of under 1 MB).
        dotnet publish src/Pulse/Pulse.csproj -c $Configuration -r $Runtime -p:SelfContained=false -p:PublishSelfContained=false `
            -p:PublishSingleFile=true -p:DebugType=embedded -o $output --nologo
        if ($LASTEXITCODE) { throw 'Publish failed.' }
        Write-Host "Published $(Join-Path $output 'tpulse.exe')"
    }
}
finally {
    Pop-Location
}
