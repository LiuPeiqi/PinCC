#!/bin/bash
# --local stays offline. --developer-id explicitly submits a signed DMG to Apple.
# Signing rules: https://developer.apple.com/library/archive/technotes/tn2206/
# Notarization: https://developer.apple.com/documentation/security/customizing-the-notarization-workflow
set -euo pipefail
usage() {
    printf '%s\n' \
        'Usage:' \
        '  bash scripts/release.sh --local [--version 0.1.0] [--build 1]' \
        '  bash scripts/release.sh --developer-id "Developer ID Application: NAME (TEAM)" --notary-profile PROFILE [--version 0.1.0] [--build 1]' \
        'The Developer ID mode uploads the DMG to Apple using a previously saved notarytool Keychain profile.'
}
mode=""
identity=""
notary_profile=""
version=""
build_number=""
while [[ "$#" -gt 0 ]]; do
    case "$1" in
        --local) [[ -z "$mode" ]] || { usage >&2; exit 2; }; mode="local"; shift ;;
        --developer-id) [[ "$#" -ge 2 && -z "$mode" ]] || { usage >&2; exit 2; }; mode="developer-id"; identity="$2"; shift 2 ;;
        --notary-profile) [[ "$#" -ge 2 ]] || { usage >&2; exit 2; }; notary_profile="$2"; shift 2 ;;
        --version) [[ "$#" -ge 2 ]] || { usage >&2; exit 2; }; version="$2"; shift 2 ;;
        --build) [[ "$#" -ge 2 ]] || { usage >&2; exit 2; }; build_number="$2"; shift 2 ;;
        --help) usage; exit 0 ;;
        *) usage >&2; exit 2 ;;
    esac
done
[[ -n "$mode" ]] || { usage >&2; exit 2; }
if [[ "$mode" == "developer-id" ]]; then
    [[ "$identity" == "Developer ID Application: "* && -n "$notary_profile" ]] || { printf '%s\n' 'A full Developer ID Application identity and a notarytool Keychain profile are required.' >&2; exit 2; }
else
    [[ -z "$notary_profile" ]] || { printf '%s\n' '--notary-profile requires --developer-id.' >&2; exit 2; }
    identity="-"
fi
[[ -z "$version" || "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { printf '%s\n' '--version must contain three numeric components.' >&2; exit 2; }
[[ -z "$build_number" || "$build_number" =~ ^[1-9][0-9]{0,3}$ ]] || { printf '%s\n' '--build must be an integer from 1 to 9999.' >&2; exit 2; }
source "$(dirname "$0")/common.sh"
require_mac_tools
prepare_build_directory
build_settings=("CODE_SIGNING_ALLOWED=NO" "CODE_SIGNING_REQUIRED=NO")
if [[ -n "$version" ]]; then build_settings+=("MARKETING_VERSION=$version"); fi
if [[ -n "$build_number" ]]; then build_settings+=("CURRENT_PROJECT_VERSION=$build_number"); fi
# Contract fingerprints, Swift tests and both native architectures must pass before packaging.
/bin/bash "$mac_dir/scripts/check.sh"
/bin/bash "$mac_dir/scripts/build.sh" Release "arm64 x86_64" "${build_settings[@]}"

source_app="$build_dir/DerivedData-Release-arm64-x86_64/Build/Products/Release/PinCC.app"
[[ -d "$source_app" && ! -L "$source_app" ]] || { printf '%s\n' 'Missing Release app bundle.' >&2; exit 1; }
output_dir="$(mktemp -d "$build_dir/release.XXXXXX")"
stage="$output_dir/stage"
mkdir "$stage"
/usr/bin/ditto "$source_app" "$stage/PinCC.app"
app="$stage/PinCC.app"
info="$app/Contents/Info.plist"
actual_version="$(/usr/bin/plutil -extract CFBundleShortVersionString raw -o - "$info")"
actual_build="$(/usr/bin/plutil -extract CFBundleVersion raw -o - "$info")"
bundle_id="$(/usr/bin/plutil -extract CFBundleIdentifier raw -o - "$info")"
executable="$(/usr/bin/plutil -extract CFBundleExecutable raw -o - "$info")"
[[ "$actual_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ && "$actual_build" =~ ^[1-9][0-9]{0,3}$ ]] || { printf '%s\n' 'Invalid release version/build in the generated app Info.plist.' >&2; exit 1; }
[[ "$executable" == "PinCC" && "$bundle_id" == "io.pincc.PinCC" ]] || { printf '%s\n' 'Unexpected release executable or bundle identifier.' >&2; exit 1; }
[[ -z "$version" || "$version" == "$actual_version" ]] || exit 1
[[ -z "$build_number" || "$build_number" == "$actual_build" ]] || exit 1
[[ "$(/usr/bin/plutil -extract LSUIElement raw -o - "$info")" == "true" ]] || { printf '%s\n' 'Release must remain a floating-card accessory app.' >&2; exit 1; }
helper="$app/Contents/Helpers/pincc"
[[ -x "$helper" && -d "$app/Contents/Resources/builtin" ]] || { printf '%s\n' 'Release is missing its CLI helper or built-in resources.' >&2; exit 1; }
for binary in "$app/Contents/MacOS/$executable" "$helper"; do
    /usr/bin/lipo -verify_arch arm64 x86_64 "$binary"
    architecture_count="$(/usr/bin/lipo -archs "$binary" | /usr/bin/awk '{ print NF }')"
    [[ "$architecture_count" -eq 2 ]] || { printf 'Unexpected architecture count: %s\n' "$binary" >&2; exit 1; }
done

sign_options=(--force --sign "$identity" --options runtime --entitlements "$mac_dir/Resources/PinCC.entitlements")
if [[ "$mode" == "developer-id" ]]; then sign_options+=(--timestamp); else sign_options+=(--timestamp=none); fi
# All package libraries are static. Sign the only nested executable before the app.
/usr/bin/codesign "${sign_options[@]}" --identifier "$bundle_id.cli" "$helper"
/usr/bin/codesign "${sign_options[@]}" "$app"
for binary in "$helper" "$app"; do
    /usr/bin/codesign --verify --strict --verbose=2 "$binary"
    signature_info="$(/usr/bin/codesign --display --verbose=4 "$binary" 2>&1)"
    printf '%s\n' "$signature_info"
    printf '%s\n' "$signature_info" | /usr/bin/grep -F '(runtime)' >/dev/null || { printf '%s\n' 'Hardened Runtime is missing.' >&2; exit 1; }
    if [[ "$mode" == "developer-id" ]]; then
        printf '%s\n' "$signature_info" | /usr/bin/grep -F 'Authority=Developer ID Application:' >/dev/null || exit 1
        printf '%s\n' "$signature_info" | /usr/bin/grep -E '^Timestamp=' >/dev/null || { printf '%s\n' 'A secure signing timestamp is required.' >&2; exit 1; }
    fi
done
/usr/bin/codesign --verify --deep --strict --verbose=2 "$app"
/usr/bin/codesign --display --entitlements - --xml "$app" > "$output_dir/entitlements.plist" 2>/dev/null
/usr/bin/plutil -lint "$output_dir/entitlements.plist"
get_task_allow="$(/usr/libexec/PlistBuddy -c 'Print :com.apple.security.get-task-allow' "$output_dir/entitlements.plist" 2>/dev/null || true)"
if [[ "$get_task_allow" == "true" ]]; then
    printf '%s\n' 'A release must not include get-task-allow.' >&2
    exit 1
fi

artifact_name="PinCC-$actual_version-$actual_build-universal"
dist_dir="$repo_dir/dist/mac"
if [[ "$mode" == "developer-id" ]]; then
    [[ ! -L "$repo_dir/dist" && ! -L "$dist_dir" ]] || fail 'Refusing symlinked distribution directories.'
    mkdir -p "$dist_dir"
    [[ "$(cd "$dist_dir" && pwd -P)" == "$repo_dir/dist/mac" ]] || fail 'Unexpected distribution path.'
fi
if [[ "$mode" == "local" ]]; then
    dist_dir="$build_dir/local"
    [[ ! -L "$dist_dir" ]] || fail 'Refusing a symlinked local artifact directory.'
    mkdir -p "$dist_dir"
    [[ "$(cd "$dist_dir" && pwd -P)" == "$build_dir/local" ]] || fail 'Unexpected local artifact path.'
    release_path="$dist_dir/$artifact_name-local-unnotarized.zip"
else
    release_path="$dist_dir/$artifact_name.dmg"
fi
[[ ! -e "$release_path" && ! -L "$release_path" ]] || { printf 'Release already exists: %s\nIncrease --version or --build; an existing release is never overwritten.\n' "$release_path" >&2; exit 1; }

release_candidate=""
cleanup_candidate() {
    # Only remove the unique file allocated by this invocation in the checked dist directory.
    if [[ -n "$release_candidate" ]]; then
        case "$release_candidate" in
            "$dist_dir"/.PinCC-*) [[ ! -L "$release_candidate" ]] && rm -f -- "$release_candidate" ;;
            *) printf '%s\n' 'Refusing unexpected distribution cleanup path.' >&2 ;;
        esac
    fi
}
trap cleanup_candidate EXIT
publish_artifact() {
    local source_file="$1"
    local source_hash candidate_hash
    [[ ! -e "$release_path" && ! -L "$release_path" ]] || { printf 'Release already exists: %s\nIncrease --version or --build.\n' "$release_path" >&2; return 1; }
    release_candidate="$(mktemp "$dist_dir/.$artifact_name.XXXXXX")"
    /usr/bin/ditto "$source_file" "$release_candidate"
    chmod 0644 "$release_candidate"
    source_hash="$(/usr/bin/shasum -a 256 "$source_file" | /usr/bin/awk '{print $1}')"
    candidate_hash="$(/usr/bin/shasum -a 256 "$release_candidate" | /usr/bin/awk '{print $1}')"
    [[ "$source_hash" == "$candidate_hash" ]] || { printf '%s\n' 'Distribution copy checksum mismatch.' >&2; return 1; }
    # link(2) publishes the complete candidate atomically and fails if the name already exists.
    # Both paths share a directory/filesystem; a concurrent publisher cannot replace this release.
    if ! /bin/ln -h "$release_candidate" "$release_path"; then
        printf 'Release was not promoted: %s\nAn existing release is never overwritten.\n' "$release_path" >&2
        return 1
    fi
    [[ -f "$release_path" && ! -L "$release_path" && "$release_candidate" -ef "$release_path" ]] || { printf '%s\n' 'Unexpected distribution destination.' >&2; return 1; }
    rm -- "$release_candidate"
    release_candidate=""
    /usr/bin/shasum -a 256 "$release_path"
}
if [[ "$mode" == "local" ]]; then
    artifact="$output_dir/$artifact_name-local-unnotarized.zip"
    /usr/bin/ditto -c -k --sequesterRsrc --keepParent "$app" "$artifact"
    publish_artifact "$artifact"
    printf 'Local ad-hoc build (unnotarized): %s\n' "$release_path"
    exit 0
fi

notarize() {
    local input_file="$1"
    local label="$2"
    local result_file="$output_dir/$label-notary-result.plist"
    local log_file="$output_dir/$label-notary-log.json"
    local status submission_id
    printf 'Submitting %s to Apple for notarization...\n' "$label"
    if ! /usr/bin/xcrun notarytool submit "$input_file" --keychain-profile "$notary_profile" --wait --output-format plist > "$result_file"; then
        printf 'Notary upload or wait failed; inspect %s\n' "$result_file" >&2
        return 1
    fi
    status="$(/usr/bin/plutil -extract status raw -o - "$result_file")"
    submission_id="$(/usr/bin/plutil -extract id raw -o - "$result_file")"
    /usr/bin/xcrun notarytool log "$submission_id" --keychain-profile "$notary_profile" "$log_file"
    [[ "$status" == "Accepted" ]] || { printf 'Notarization returned %s; inspect %s\n' "$status" "$log_file" >&2; return 1; }
}

# The app ticket must be inside the final disk image, so notarize and staple it first.
app_submission="$output_dir/$artifact_name-notary-app.zip"
/usr/bin/ditto -c -k --sequesterRsrc --keepParent "$app" "$app_submission"
notarize "$app_submission" app
/usr/bin/xcrun stapler staple "$app"
/usr/bin/xcrun stapler validate "$app"
/usr/bin/codesign --verify --deep --strict --verbose=2 "$app"
/usr/sbin/spctl --assess --type execute --verbose=2 "$app"

/usr/bin/ditto "$mac_dir/Resources/INSTALL.txt" "$stage/安装与升级.txt"
ln -s /Applications "$stage/Applications"
artifact="$output_dir/$artifact_name.dmg"
/usr/bin/hdiutil create -volname "PinCC $actual_version ($actual_build)" -srcfolder "$stage" -format UDZO "$artifact"
/usr/bin/codesign --force --sign "$identity" --timestamp --identifier "$bundle_id.dmg" "$artifact"
/usr/bin/codesign --verify --strict --verbose=2 "$artifact"
/usr/bin/hdiutil verify "$artifact"
notarize "$artifact" dmg
/usr/bin/xcrun stapler staple "$artifact"
/usr/bin/xcrun stapler validate "$artifact"
/usr/bin/codesign --verify --strict --verbose=2 "$artifact"
/usr/sbin/spctl --assess --type open --context context:primary-signature --verbose=2 "$artifact"
publish_artifact "$artifact"
printf 'Signed, notarized Universal release: %s\n' "$release_path"
