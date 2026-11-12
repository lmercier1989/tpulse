<#
.SYNOPSIS
    Installs tpulse for your user: the program, a "Pulse" profile in Windows Terminal, a Start menu
    shortcut and a PATH entry. Nothing is written outside your user profile; no administrator rights.

.DESCRIPTION
    * tpulse.exe goes to -Destination (default %LOCALAPPDATA%\Programs\Pulse), which is added to your
      user PATH, so `tpulse` works in every new terminal.
    * The Windows Terminal profile is added as a fragment (%LOCALAPPDATA%\Microsoft\Windows Terminal\
      Fragments\Pulse), so your own settings.json is never edited. Pick "Pulse" from the + menu and the
      monitor opens straight away, in the instrument colours and the chosen font.
    * The Start menu shortcut "Pulse" opens that profile.

.PARAMETER Font
    Font of the Pulse profile. Default: Consolas, the monospace font every Windows version ships with; its
    narrow, ClearType-tuned letters keep the labels crisp and leave room for the details. Any installed
    font works, for example "Cascadia Mono".

.PARAMETER FontWeight
    Font weight of the Pulse profile: thin, extra-light, light, semi-light, normal, medium, semi-bold, bold.

.PARAMETER FontSize
    Font size of the Pulse profile in points.

.PARAMETER Uninstall
    Removes the program, the profile, the shortcut and the PATH entry. Settings and logs in
    %LOCALAPPDATA%\Pulse are kept.

.EXAMPLE
    ./install.ps1

.EXAMPLE
    ./install.ps1 -Font "Cascadia Mono" -FontWeight semi-light -FontSize 11

.EXAMPLE
    ./install.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [string] $Destination = (Join-Path $env:LOCALAPPDATA 'Programs\Pulse'),

    [ValidateSet('win-x64', 'win-arm64')]
    [string] $Runtime = 'win-x64',

    [string] $Font = 'Consolas',

    [ValidateSet('thin', 'extra-light', 'light', 'semi-light', 'normal', 'medium', 'semi-bold', 'bold')]
    [string] $FontWeight = 'normal',

    [ValidateRange(6, 36)]
    [double] $FontSize = 11,

    [switch] $Uninstall
)

$ErrorActionPreference = 'Stop'

# A fixed id, so reinstalling updates the same profile instead of adding another one.
$profileGuid = '{6a3c58d1-0f2e-4b7c-9d1a-5e8f2c4b7a90}'
$folder = [IO.Path]::GetFullPath($Destination).TrimEnd('\')
$fragmentFolder = Join-Path $env:LOCALAPPDATA 'Microsoft\Windows Terminal\Fragments\Pulse'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Pulse.lnk'
$terminal = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\wt.exe'
$terminalSettings = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Packages\Microsoft.WindowsTerminal*\LocalState\settings.json') -ErrorAction SilentlyContinue |
    Select-Object -First 1

$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
$entries = @(($userPath -split ';') | Where-Object { $_ })
$onPath = @($entries | Where-Object { $_.TrimEnd('\') -ieq $folder }).Count -gt 0

function Update-Terminal {
    # Windows Terminal reloads its settings, fragments included, when settings.json changes; touching the
    # file's time is enough, its content stays as it is.
    if ($terminalSettings) {
        (Get-Item $terminalSettings.FullName).LastWriteTime = Get-Date
    }
}

if (Get-Process tpulse -ErrorAction SilentlyContinue) {
    throw 'tpulse is running. Close it first, then run this script again.'
}

if ($Uninstall) {
    foreach ($path in $folder, $fragmentFolder, $shortcut) {
        if (Test-Path $path) {
            Remove-Item $path -Recurse -Force
        }
    }

    if ($onPath) {
        $remaining = $entries | Where-Object { $_.TrimEnd('\') -ine $folder }
        [Environment]::SetEnvironmentVariable('Path', ($remaining -join ';'), 'User')
    }

    Update-Terminal
    Write-Host 'Removed tpulse, its Windows Terminal profile, its shortcut and its PATH entry.'
    return
}

& (Join-Path $PSScriptRoot 'build.ps1') -Configuration Release -Publish -Runtime $Runtime -SkipTests

# Checked again: building takes a while, and a running tpulse locks the files that are about to be replaced.
if (Get-Process tpulse -ErrorAction SilentlyContinue) {
    throw 'tpulse was started while building. Close it (q), then run this script again; nothing was changed.'
}

if (Test-Path $folder) {
    Get-ChildItem $folder | Remove-Item -Recurse -Force
}

New-Item -ItemType Directory -Force $folder | Out-Null
Copy-Item (Join-Path $PSScriptRoot "artifacts\$Runtime\*") $folder -Recurse -Force
$exe = Join-Path $folder 'tpulse.exe'
$icon = Join-Path $folder 'pulse.ico'

if (-not $onPath) {
    [Environment]::SetEnvironmentVariable('Path', (($entries + $folder) -join ';'), 'User')
    Write-Host "Added $folder to your user PATH."
}

# The Windows Terminal profile: the instrument background around the graphs, no scroll bar, the tab named
# Pulse, and the tab closing when you quit with q.
$fragment = [ordered]@{
    profiles = @(
        [ordered]@{
            guid                     = $profileGuid
            name                     = 'Pulse'
            commandline              = "`"$exe`""
            icon                     = $icon
            startingDirectory        = '%USERPROFILE%'
            tabTitle                 = 'Pulse'
            suppressApplicationTitle = $true
            font                     = [ordered]@{ face = $Font; size = $FontSize; weight = $FontWeight }
            antialiasingMode         = 'cleartype'
            padding                  = '8, 6'
            background               = '#090B0F'
            foreground               = '#E4E8EE'
            useAcrylic               = $false
            opacity                  = 100
            scrollbarState           = 'hidden'
            bellStyle                = 'none'
            closeOnExit              = 'graceful'
        }
    )
}
New-Item -ItemType Directory -Force $fragmentFolder | Out-Null
[IO.File]::WriteAllText((Join-Path $fragmentFolder 'pulse.json'), ($fragment | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding $false))
Update-Terminal
Write-Host "Added the Pulse profile to Windows Terminal ($Font, $FontWeight, $FontSize pt)."

# The Start menu shortcut opens the profile in Windows Terminal, or tpulse in a console without it.
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
if (Test-Path $terminal) {
    $link.TargetPath = $terminal
    $link.Arguments = '-p "Pulse"'
}
else {
    $link.TargetPath = $exe
    $link.Arguments = ''
}
$link.IconLocation = "$icon,0"
$link.Description = 'Pulse system monitor'
$link.WorkingDirectory = $env:USERPROFILE
$link.Save()
Write-Host 'Added Pulse to the Start menu.'

Write-Host "Installed tpulse to $folder. In Windows Terminal, choose Pulse from the + menu, or run tpulse in a new tab."
