#!/bin/bash
# Xcode 16+, Apple Swift 6, macOS 13+. No remote package dependencies.
set -euo pipefail

usage() {
    printf '%s\n' 'Usage: bash scripts/build.sh [Debug|Release] ["arm64 x86_64"] [SETTING=value ...]'
}

if [[ "$#" -gt 0 && "$1" == "--help" ]]; then usage; exit 0; fi
configuration="${1:-Debug}"
if [[ "$#" -gt 0 ]]; then shift; fi
case "$configuration" in Debug|Release) ;; *) usage >&2; exit 2 ;; esac
if [[ "$configuration" == "Release" ]]; then architectures="arm64 x86_64"; else architectures="$(uname -m)"; fi
if [[ "$#" -gt 0 && "$1" != *=* ]]; then architectures="$1"; shift; fi
case "$architectures" in arm64|x86_64|"arm64 x86_64"|"x86_64 arm64") ;; *) printf 'Unsupported architectures: %s\n' "$architectures" >&2; exit 2 ;; esac
for setting in "$@"; do
    [[ "$setting" =~ ^[A-Za-z_][A-Za-z0-9_]*= ]] || { usage >&2; exit 2; }
done

source "$(dirname "$0")/common.sh"
require_mac_tools
prepare_build_directory
architecture_label="${architectures// /-}"
derived_data="$build_dir/DerivedData-$configuration-$architecture_label"
source_packages="$build_dir/SourcePackages"
[[ ! -L "$derived_data" && ! -L "$source_packages" ]] || { printf '%s\n' 'Refusing symlinked build output directories.' >&2; exit 1; }

/usr/bin/xcodebuild \
    -project "$mac_dir/PinCC.xcodeproj" -scheme PinCC \
    -configuration "$configuration" -destination 'generic/platform=macOS' \
    -derivedDataPath "$derived_data" -clonedSourcePackagesDir "$source_packages" \
    -disableAutomaticPackageResolution -skipPackageUpdates \
    build ARCHS="$architectures" ONLY_ACTIVE_ARCH=NO "$@"
printf 'App: %s\n' "$derived_data/Build/Products/$configuration/PinCC.app"
