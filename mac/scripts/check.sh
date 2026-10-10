#!/bin/bash
# Tests and both compile architectures use disposable data under artifacts/macos.
set -euo pipefail
if [[ "$#" -gt 0 ]]; then
    printf '%s\n' 'Usage: bash scripts/check.sh' >&2
    exit 2
fi
source "$(dirname "$0")/common.sh"
require_mac_tools
prepare_build_directory
check_run="$(mktemp -d "$build_dir/check.XXXXXX")"
check_home="$(mktemp -d "$build_dir/check-home.XXXXXX")"
cleanup() {
    # Delete only the unique directory allocated by this invocation.
    if [[ -d "$check_home" && ! -L "$check_home" ]]; then
        resolved_home="$(cd "$check_home" && pwd -P)"
        case "$resolved_home" in "$build_dir"/check-home.*) rm -rf -- "$resolved_home" ;; *) printf '%s\n' 'Refusing unexpected test cleanup path.' >&2 ;; esac
    fi
}
trap cleanup EXIT
export PINCC_HOME="$check_home/home"
[[ ! -L "$build_dir/SwiftPM" ]] || { printf '%s\n' 'Refusing a symlinked SwiftPM output directory.' >&2; exit 1; }

/usr/bin/xcrun python3 "$mac_dir/scripts/check-source.py" 2>&1 | /usr/bin/tee "$check_run/source-check.log"
/usr/bin/plutil -lint "$mac_dir/Resources/Info.plist" "$mac_dir/Resources/PinCC.entitlements" "$mac_dir/PinCC.xcodeproj/project.pbxproj"
/usr/bin/xcrun swift test --package-path "$mac_dir" --scratch-path "$build_dir/SwiftPM" \
    --cache-path "$build_dir/SwiftPM/cache" --config-path "$build_dir/SwiftPM/config" \
    --security-path "$build_dir/SwiftPM/security" --manifest-cache local \
    2>&1 | /usr/bin/tee "$check_run/swift-test.log"
/bin/bash "$mac_dir/scripts/build.sh" Debug arm64 2>&1 | /usr/bin/tee "$check_run/build-arm64.log"
/bin/bash "$mac_dir/scripts/build.sh" Debug x86_64 2>&1 | /usr/bin/tee "$check_run/build-x86_64.log"

native_arch="$(uname -m)"
native_app="$build_dir/DerivedData-Debug-$native_arch/Build/Products/Debug/PinCC.app"
/usr/bin/codesign --verify --deep --strict --verbose=2 "$native_app"
"$native_app/Contents/Helpers/pincc" --help
printf 'Source/contracts, Swift tests, arm64/x86_64 app builds, bundle signature, and CLI entry point passed.\nLogs: %s\n' "$check_run"
