#!/bin/sh
# Installs tpulse for your user on Linux: the program in ~/.local/share/pulse and a `tpulse` command in
# ~/.local/bin. Nothing is written outside your home folder; no root needed. Needs the .NET 10 SDK to build.
#
#   ./install.sh               build and install
#   ./install.sh --uninstall   remove the program and the command (settings and logs in ~/.local/share/Pulse stay)
#
# PULSE_DESTINATION and PULSE_BIN override the two folders.
set -eu

destination=${PULSE_DESTINATION:-"${XDG_DATA_HOME:-$HOME/.local/share}/pulse"}
bin=${PULSE_BIN:-"$HOME/.local/bin"}
here=$(cd "$(dirname "$0")" && pwd)

if [ "${1:-}" = "--uninstall" ] || [ "${1:-}" = "-Uninstall" ]; then
    rm -f "$bin/tpulse"
    rm -rf "$destination"
    echo "Pulse removed. Settings and logs in ${XDG_DATA_HOME:-$HOME/.local/share}/Pulse are kept."
    exit 0
fi

if ! command -v dotnet >/dev/null 2>&1; then
    echo "The .NET 10 SDK is needed to build Pulse: https://dotnet.microsoft.com/download/dotnet/10.0" >&2
    exit 1
fi

case "$(uname -m)" in
    x86_64) runtime=linux-x64 ;;
    aarch64 | arm64) runtime=linux-arm64 ;;
    *) echo "Pulse is built for x86_64 and aarch64 Linux; this machine is $(uname -m)." >&2; exit 1 ;;
esac

# Framework-dependent single file: small, and it runs on the .NET runtime the SDK installed.
dotnet publish "$here/src/Pulse/Pulse.csproj" -c Release -r "$runtime" -p:SelfContained=false -p:PublishSelfContained=false \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=embedded -o "$destination" --nologo

mkdir -p "$bin"
ln -sf "$destination/tpulse" "$bin/tpulse"
echo "Installed $destination/tpulse as $bin/tpulse."

case ":$PATH:" in
    *":$bin:"*) echo "Type tpulse in a terminal to start it." ;;
    *) echo "Add $bin to your PATH (most distributions do this at your next login), then type tpulse." ;;
esac
