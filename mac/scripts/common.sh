#!/bin/bash
# Shared local Apple-tool preflight and output paths; compatible with macOS Bash 3.2.
set -euo pipefail
mac_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
repo_dir="$(cd "$mac_dir/.." && pwd -P)"
build_dir="$repo_dir/artifacts/macos"

fail() { printf 'Error: %s\n' "$*" >&2; exit 1; }
require_mac_tools() {
    [[ "$(uname -s)" == "Darwin" ]] || fail 'Run this on a Mac with the full Xcode application installed.'
    local xcode_version xcode_major swift_version swift_major
    xcode_version="$(/usr/bin/xcodebuild -version)" || fail 'Select the full Xcode application with xcode-select.'
    xcode_major="$(printf '%s\n' "$xcode_version" | /usr/bin/awk '/^Xcode / { split($2, version, "."); print version[1]; exit }')"
    [[ "$xcode_major" =~ ^[0-9]+$ && "$xcode_major" -ge 16 ]] || fail 'Xcode 16 or newer is required.'
    swift_version="$(/usr/bin/xcrun swiftc --version)" || fail 'The selected Xcode must provide Apple Swift.'
    swift_major="$(printf '%s\n' "$swift_version" | /usr/bin/sed -nE 's/.*Apple Swift version ([0-9]+).*/\1/p' | /usr/bin/head -n 1)"
    [[ "$swift_major" =~ ^[0-9]+$ && "$swift_major" -ge 6 ]] || fail 'The selected Xcode must provide Apple Swift 6 or newer.'
    /usr/bin/xcrun --sdk macosx --show-sdk-path >/dev/null || fail 'The selected Xcode has no macOS SDK.'
}
prepare_build_directory() {
    [[ ! -L "$repo_dir/artifacts" && ! -L "$build_dir" ]] || fail 'Refusing symlinked artifacts/macos output directories.'
    mkdir -p "$build_dir"
    [[ "$(cd "$build_dir" && pwd -P)" == "$repo_dir/artifacts/macos" ]] || fail 'Unexpected artifacts/macos output path.'
}
