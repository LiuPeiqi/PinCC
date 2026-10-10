#!/usr/bin/env python3
"""Dependency-free source/package checks; does not replace Xcode's type checker."""
from __future__ import annotations
import argparse
import hashlib
import json
from pathlib import Path
import plistlib
import re
import struct
import sys
import xml.etree.ElementTree as ET

MAC = Path(__file__).resolve().parents[1]
ROOT = MAC.parent


class OpenStepParser:
    """Read Xcode project dictionaries and arrays, rather than matching strings."""

    def __init__(self, text: str):
        pattern = r'\s+|/\*.*?\*/|//[^\n]*|"(?:\\.|[^"\\])*"|[{}()=;,]|[^\s{}()=;," ]+'
        self.tokens: list[str] = []
        position = 0
        for match in re.finditer(pattern, text, re.DOTALL):
            require(match.start() == position, f"invalid project syntax at character {position}")
            position = match.end()
            token = match.group()
            require(not token.startswith("/*") or token.endswith("*/"), "unterminated project comment")
            if not token.isspace() and not token.startswith(("/*", "//")):
                self.tokens.append(token)
        require(position == len(text), f"invalid project syntax at character {position}")
        self.index = 0

    def peek(self) -> str:
        require(self.index < len(self.tokens), "unexpected end of project")
        return self.tokens[self.index]

    def take(self, expected: str | None = None) -> str:
        token = self.peek()
        self.index += 1
        require(expected is None or token == expected, f"expected {expected!r}, found {token!r}")
        return token

    def value(self):
        token = self.take()
        if token == "{":
            result = {}
            while self.peek() != "}":
                key = self.take()
                require(key not in {"{", "}", "(", ")", "=", ";", ","}, "invalid project dictionary key")
                key = json.loads(key) if key.startswith('"') else key
                require(key not in result, f"duplicate project key: {key}")
                self.take("=")
                result[key] = self.value()
                self.take(";")
            self.take("}")
            return result
        if token == "(":
            result = []
            while self.peek() != ")":
                result.append(self.value())
                if self.peek() != ")":
                    self.take(",")
            self.take(")")
            return result
        require(token not in {"}", "(", ")", "=", ";", ","}, f"unexpected project token: {token}")
        return json.loads(token) if token.startswith('"') else token

    def parse(self) -> dict:
        document = self.value()
        require(isinstance(document, dict), "project must be a dictionary")
        require(self.index == len(self.tokens), "extra tokens after project")
        return document


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def read_xcconfig(path: Path, stack: tuple[Path, ...] = ()) -> dict[str, str]:
    """Resolve includes before checking the effective project/target settings."""
    path = path.resolve()
    require(path not in stack, f"cyclic xcconfig include: {path.name}")
    settings = {}
    for line in path.read_text(encoding="utf-8").splitlines():
        line = re.sub(r"\s+//.*$", "", line).strip()
        if not line or line.startswith("//"):
            continue
        include = re.fullmatch(r'#include(\?)?\s+"([^"]+)"', line)
        if include:
            included = path.parent / include[2]
            if include[1] and not included.is_file():
                continue
            require(included.is_file(), f"missing xcconfig include: {included}")
            settings.update(read_xcconfig(included, (*stack, path)))
            continue
        assignment = re.fullmatch(r"([A-Z][A-Z0-9_]*)\s*=\s*(.*?)\s*;?", line)
        require(assignment is not None, f"unsupported xcconfig statement in {path.name}: {line}")
        settings[assignment[1]] = assignment[2]
    return settings


def validate_project(mac: Path = MAC) -> None:
    document = OpenStepParser((mac / "PinCC.xcodeproj/project.pbxproj").read_text(encoding="utf-8")).parse()
    objects = document["objects"]
    require(isinstance(objects, dict) and objects, "project has no objects")
    require(all(re.fullmatch(r"[A-Fa-f0-9]{24}", key) for key in objects), "invalid project object identifier")
    require(all(isinstance(obj, dict) and "isa" in obj for obj in objects.values()), "invalid project object")

    def references(value):
        if isinstance(value, dict):
            for key, child in value.items():
                yield from references(key)
                yield from references(child)
        elif isinstance(value, list):
            for child in value:
                yield from references(child)
        elif isinstance(value, str) and re.fullmatch(r"[A-Fa-f0-9]{24}", value):
            yield value

    missing = set(references(document)) - set(objects)
    require(not missing, "unresolved project object references: " + ", ".join(sorted(missing)))
    project = objects[document["rootObject"]]
    require(project["isa"] == "PBXProject", "root object must be PBXProject")
    require(project["attributes"].get("LastUpgradeCheck") == "2600", "project must acknowledge Xcode 26 recommended settings")
    require(not any(obj["isa"] == "XCRemoteSwiftPackageReference" for obj in objects.values()), "remote Xcode package dependency")
    require(not any(obj["isa"] == "PBXShellScriptBuildPhase" for obj in objects.values()), "native targets must use standard Xcode build phases")

    paths: dict[str, Path] = {}
    visited: set[str] = set()

    def visit(key: str, parent: Path) -> None:
        require(key not in visited, f"duplicate or cyclic project group membership: {key}")
        visited.add(key)
        obj = objects[key]
        tree = obj.get("sourceTree", "<group>")
        path = ((mac if tree == "SOURCE_ROOT" else parent) / obj.get("path", "")).resolve()
        if obj["isa"] in {"PBXGroup", "PBXVariantGroup"}:
            require(tree in {"<group>", "SOURCE_ROOT"}, f"unsupported group source tree: {tree}")
            for child in obj.get("children", []):
                visit(child, path)
        elif obj["isa"] in {"PBXFileReference", "PBXFileSystemSynchronizedRootGroup"}:
            if tree in {"<group>", "SOURCE_ROOT"}:
                require(path.exists(), f"missing referenced path: {path}")
                paths[key] = path
            else:
                require(tree == "BUILT_PRODUCTS_DIR", f"unsupported file source tree: {tree}")
            if obj["isa"] == "PBXFileSystemSynchronizedRootGroup":
                require(path.is_dir(), f"synchronized group is not a directory: {path}")
                require(not obj.get("exceptions"), "synchronized groups must not exclude app/helper sources")
        else:
            raise ValueError(f"unexpected object in project group: {obj['isa']}")

    visit(project["mainGroup"], mac)
    target_ids = {objects[key]["name"]: key for key in project["targets"]}
    require(len(target_ids) == len(project["targets"]) == 2, "expected two distinct native targets")
    require(set(target_ids) == {"PinCCApp", "pincc"}, "expected PinCCApp and pincc targets; SwiftPM owns the tests")
    targets = {name: objects[key] for name, key in target_ids.items()}
    app, helper = targets["PinCCApp"], targets["pincc"]
    for name, target in targets.items():
        require(target["isa"] == "PBXNativeTarget", f"{name} must be a native target")
        groups = target.get("fileSystemSynchronizedGroups", [])
        expected_path = (mac / f"Sources/{name}").resolve()
        require(len(groups) == 1 and paths.get(groups[0]) == expected_path, f"{name} must synchronize its own source directory")
        require(objects[groups[0]]["isa"] == "PBXFileSystemSynchronizedRootGroup", f"{name} source membership must be synchronized")
        require((expected_path / "main.swift").is_file(), f"missing {name} entry point")
        phases = [objects[key] for key in target["buildPhases"]]
        require(len(target["buildPhases"]) == len(set(target["buildPhases"])), f"duplicate {name} build phase")
        sources = [phase for phase in phases if phase["isa"] == "PBXSourcesBuildPhase"]
        require(len(sources) == 1 and sources[0]["files"] == [], f"{name} source phase must use synchronized membership without duplicate explicit files")
        frameworks = [phase for phase in phases if phase["isa"] == "PBXFrameworksBuildPhase"]
        require(len(frameworks) == 1, f"{name} needs one frameworks phase")
        dependencies = target["packageProductDependencies"]
        linked = [objects[key].get("productRef") for key in frameworks[0]["files"]]
        require(set(linked) == set(dependencies) and len(linked) == len(dependencies), f"{name} package products must be linked once")
        expected_products = {"PinCCCli"} if name == "pincc" else {"PinCCCore", "PinCCDesk", "PinCCMacPlatform", "PinCCCli"}
        require({objects[key]["productName"] for key in dependencies} == expected_products and len(dependencies) == len(expected_products), f"incorrect {name} local package products")
    require(app["productType"] == "com.apple.product-type.application", "PinCCApp must be an app")
    require(helper["productType"] == "com.apple.product-type.tool", "pincc must be a native command-line executable")
    for target, product in [(app, "PinCC.app"), (helper, "pincc")]:
        reference = objects[target["productReference"]]
        require(reference["isa"] == "PBXFileReference" and reference["path"] == product and reference["sourceTree"] == "BUILT_PRODUCTS_DIR", f"invalid built product reference: {product}")

    packages = project["packageReferences"]
    require(len(packages) == 1, "project must reference one local Swift package")
    package = objects[packages[0]]
    require(package["isa"] == "XCLocalSwiftPackageReference" and (mac / package["relativePath"]).resolve() == mac.resolve(), "Xcode must reference mac/Package.swift as its local package")
    for target in targets.values():
        for key in target["packageProductDependencies"]:
            require(objects[key]["isa"] == "XCSwiftPackageProductDependency" and objects[key]["package"] == packages[0], "target product belongs to a different package")
    dependencies = [objects[key] for key in app["dependencies"]]
    require(len(dependencies) == 1 and dependencies[0]["isa"] == "PBXTargetDependency" and dependencies[0]["target"] == target_ids["pincc"], "app must build its embedded pincc helper")
    proxy = objects[dependencies[0]["targetProxy"]]
    require(proxy["isa"] == "PBXContainerItemProxy" and proxy["containerPortal"] == document["rootObject"] and proxy["remoteGlobalIDString"] == target_ids["pincc"], "helper dependency proxy must resolve to this project's pincc target")
    copy_phases = [objects[key] for key in app["buildPhases"] if objects[key]["isa"] == "PBXCopyFilesBuildPhase"]
    require(len(copy_phases) == 1, "app must use one standard helper Copy Files phase")
    phase = copy_phases[0]
    require(phase["dstSubfolderSpec"] == "1" and phase["dstPath"] == "Contents/Helpers", "helper must be embedded in Contents/Helpers")
    require(phase["runOnlyForDeploymentPostprocessing"] == "0", "helper embedding must run for ordinary builds")
    require(len(phase["files"]) == 1, "only pincc belongs in the helper copy phase")
    embedded = objects[phase["files"][0]]
    require(embedded["isa"] == "PBXBuildFile" and embedded["fileRef"] == helper["productReference"], "embed the built pincc helper product")
    require(set(embedded.get("settings", {}).get("ATTRIBUTES", [])) == {"CodeSignOnCopy", "RemoveHeadersOnCopy"}, "sign pincc during embedding and remove headers")

    resources = [objects[key] for key in app["buildPhases"] if objects[key]["isa"] == "PBXResourcesBuildPhase"]
    require(len(resources) == 1, "app needs one resources phase")
    resource_paths = [paths.get(objects[key].get("fileRef")) for key in resources[0]["files"]]
    expected_resources = {(mac / "Resources/Assets.xcassets").resolve(), (mac.parent / "builtin").resolve()}
    require(set(resource_paths) == expected_resources and len(resource_paths) == len(expected_resources), "compile the app icon catalog and copy builtin templates to the app's resource directory")

    def configurations(owner: dict, name: str) -> dict[str, dict]:
        config_list = objects[owner["buildConfigurationList"]]
        require(config_list["isa"] == "XCConfigurationList", f"invalid {name} configuration list")
        items = [objects[key] for key in config_list["buildConfigurations"]]
        require(len(items) == 2 and {item["name"] for item in items} == {"Debug", "Release"}, f"{name} needs distinct Debug and Release configurations")
        require(all(item["isa"] == "XCBuildConfiguration" for item in items), f"invalid {name} build configuration")
        require(config_list["defaultConfigurationName"] == "Release", f"{name} default configuration must be Release")
        return {item["name"]: item for item in items}

    project_configs = configurations(project, "project")
    target_configs = {name: configurations(target, name) for name, target in targets.items()}
    for mode, config in project_configs.items():
        config_path = (mac / f"Config/{mode}.xcconfig").resolve()
        require(paths.get(config["baseConfigurationReference"]) == config_path, f"project {mode} must load its shared xcconfig")
        inherited = read_xcconfig(config_path)
        inherited.update(config["buildSettings"])
        for setting, expected in {
            "SWIFT_VERSION": "6.0", "MACOSX_DEPLOYMENT_TARGET": "13.0",
            "ENABLE_HARDENED_RUNTIME": "YES", "ENABLE_APP_SANDBOX": "NO",
            "COPY_PHASE_STRIP": "NO", "DEAD_CODE_STRIPPING": "YES",
            "STRING_CATALOG_GENERATE_SYMBOLS": "YES",
        }.items():
            require(inherited.get(setting) == expected, f"{mode} must set {setting} = {expected}")
        if mode == "Release":
            require(inherited.get("ARCHS") == "arm64 x86_64" and inherited.get("ONLY_ACTIVE_ARCH") == "NO", "Release must build both Mac architectures")
        for name in targets:
            item = target_configs[name][mode]
            settings = dict(inherited)
            if item.get("baseConfigurationReference"):
                settings.update(read_xcconfig(paths[item["baseConfigurationReference"]]))
            settings.update(item["buildSettings"])
            for setting in ["SWIFT_VERSION", "MACOSX_DEPLOYMENT_TARGET", "ENABLE_HARDENED_RUNTIME", "ENABLE_APP_SANDBOX", "COPY_PHASE_STRIP"]:
                require(settings.get(setting) == inherited[setting], f"{name} {mode} must preserve shared {setting}")
            expected_id = "io.pincc.PinCC" + (".dev" if mode == "Debug" else "")
            require(settings.get("PRODUCT_NAME") == ("PinCC" if name == "PinCCApp" else "pincc"), f"unstable {name} product name")
            require(settings.get("PRODUCT_BUNDLE_IDENTIFIER") == expected_id + (".cli" if name == "pincc" else ""), f"{name} identity must match its {mode} app/helper role")
            require(settings.get("SKIP_INSTALL") == ("YES" if name == "pincc" else "NO"), "archive must install only PinCC.app")
            require(settings.get("CODE_SIGN_ENTITLEMENTS") == "Resources/PinCC.entitlements", f"{name} must use the shared hardened-runtime entitlements")
            if name == "pincc":
                for setting, expected in {
                    "OTHER_CODE_SIGN_FLAGS": "$(inherited) --identifier $(PRODUCT_BUNDLE_IDENTIFIER)",
                    "STRIP_INSTALLED_PRODUCT": "NO", "DEPLOYMENT_POSTPROCESSING": "NO",
                }.items():
                    require(settings.get(setting) == expected, f"helper {mode} must set {setting} = {expected}")
            else:
                require(settings.get("ASSETCATALOG_COMPILER_APPICON_NAME") == "AppIcon", "app must compile the AppIcon set")
                require(settings.get("GENERATE_INFOPLIST_FILE") == "NO" and settings.get("INFOPLIST_FILE") == "Resources/Info.plist", "app must use its maintained Info.plist")

    scheme = ET.parse(mac / "PinCC.xcodeproj/xcshareddata/xcschemes/PinCC.xcscheme").getroot()
    require(scheme.attrib.get("LastUpgradeVersion") == "2600", "shared scheme must match Xcode 26 recommended settings")
    for reference in scheme.iter("BuildableReference"):
        key = reference.attrib["BlueprintIdentifier"]
        require(key in project["targets"], "scheme references an unknown target")
        target = objects[key]
        require(reference.attrib.get("BlueprintName") == target["name"] and reference.attrib.get("BuildableName") == objects[target["productReference"]]["path"], "scheme target name/product does not match its project reference")
        require(reference.attrib.get("ReferencedContainer") == "container:PinCC.xcodeproj", "scheme references a different project")
    build_action = scheme.find("BuildAction")
    require(build_action is not None and build_action.attrib.get("buildImplicitDependencies") == "YES", "scheme must build the app's helper dependency")
    entries = scheme.findall("BuildAction/BuildActionEntries/BuildActionEntry")
    require(len(entries) == 1 and entries[0].find("BuildableReference").attrib["BlueprintIdentifier"] == target_ids["PinCCApp"], "shared scheme must build PinCCApp with its implicit helper dependency")
    require(all(entries[0].attrib.get(flag) == "YES" for flag in ["buildForTesting", "buildForRunning", "buildForProfiling", "buildForArchiving", "buildForAnalyzing"]), "scheme must enable all app build actions")
    for action, mode in [("TestAction", "Debug"), ("LaunchAction", "Debug"), ("AnalyzeAction", "Debug"), ("ProfileAction", "Release"), ("ArchiveAction", "Release")]:
        element = scheme.find(action)
        require(element is not None and element.attrib.get("buildConfiguration") == mode, f"{action} must use {mode}")
    require(not scheme.findall("TestAction/Testables/TestableReference"), "SwiftPM tests must run through swift test")
    for action in ["LaunchAction", "ProfileAction"]:
        runnable = scheme.find(f"{action}/BuildableProductRunnable/BuildableReference")
        require(runnable is not None and runnable.attrib.get("BlueprintIdentifier") == target_ids["PinCCApp"], f"{action} must run PinCCApp")


def validate_icons(mac: Path = MAC) -> None:
    assets = mac / "Resources/Assets.xcassets/AppIcon.appiconset"
    catalog = json.loads((assets / "Contents.json").read_text(encoding="utf-8"))
    images = catalog["images"]
    expected_slots = {(f"{size}x{size}", f"{scale}x") for size in [16, 32, 128, 256, 512] for scale in [1, 2]}
    require(len(images) == 10 and {(item["size"], item["scale"]) for item in images} == expected_slots, "expected all ten distinct macOS app icon slots")
    for item in images:
        require(item.get("idiom") == "mac", "app icon slots must use the mac idiom")
        filename = item["filename"]
        require(Path(filename).name == filename, "icon filenames must stay within AppIcon.appiconset")
        data = (assets / filename).read_bytes()
        require(len(data) >= 33 and data[:8] == b"\x89PNG\r\n\x1a\n" and data[12:16] == b"IHDR", f"app icon is not a PNG: {filename}")
        pixels = int(item["size"].split("x")[0]) * int(item["scale"].removesuffix("x"))
        require(struct.unpack(">II", data[16:24]) == (pixels, pixels), f"incorrect icon size: {filename}, expected {pixels}x{pixels}")


def validate_check_script() -> None:
    check = (MAC / "scripts/check.sh").read_text(encoding="utf-8")
    source = '/usr/bin/xcrun python3 "$mac_dir/scripts/check-source.py"'
    tests = '/usr/bin/xcrun swift test --package-path "$mac_dir"'
    require('source "$(dirname "$0")/common.sh"' in check, "check.sh must load shared build paths/tool validation")
    require(source in check and tests in check and check.index(source) < check.index(tests), "check.sh must validate sources/contracts before executing SwiftPM tests")
    for arch in ["arm64", "x86_64"]:
        build = f'/bin/bash "$mac_dir/scripts/build.sh" Debug {arch}'
        require(build in check and check.index(tests) < check.index(build), f"check.sh must compile {arch} after SwiftPM tests")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--allow-stale-contracts", action="store_true", help="Report, but allow a C# baseline that needs regeneration.")
    args = parser.parse_args()
    errors: list[str] = []
    checked = 0

    def need(relative: str) -> Path:
        nonlocal checked
        p = MAC / relative
        if not p.is_file():
            errors.append(f"missing file: mac/{relative}")
        else:
            checked += 1
        return p

    for relative in [
        "Package.swift", "PinCC.xcodeproj/project.pbxproj",
        "PinCC.xcodeproj/xcshareddata/xcschemes/PinCC.xcscheme",
        "Resources/Info.plist", "Resources/PinCC.entitlements", "Resources/INSTALL.txt",
        "Sources/PinCCApp/main.swift", "Sources/pincc/main.swift",
        "Sources/CSQLite/module.modulemap", "Sources/CSQLite/shim.h",
        "contracts/source-fingerprint.json", "contracts/vectors.jsonl",
        "scripts/build.sh", "scripts/release.sh", "scripts/install-cli.sh", "scripts/check.sh", "scripts/common.sh",
        "Config/Base.xcconfig", "Config/Debug.xcconfig", "Config/Release.xcconfig",
        "Resources/Assets.xcassets/AppIcon.appiconset/Contents.json"
    ]:
        need(relative)

    for folder in ["Sources/PinCCCore", "Sources/PinCCDesk", "Sources/PinCCMacPlatform", "Sources/PinCCCli",
                   "Tests/PinCCCoreTests", "Tests/PinCCDeskTests", "Tests/PinCCCliTests", "Tests/PinCCMacPlatformTests"]:
        if not list((MAC / folder).glob("*.swift")):
            errors.append(f"empty Swift target: {folder}")

    for folder in ["Sources", "Tests", "Config", "scripts", "contracts", "Resources", "PinCC.xcodeproj"]:
        for p in (MAC / folder).rglob("*"):
            if not p.is_file() or p.suffix not in {".swift", ".h", ".modulemap", ".txt", ".sh", ".py", ".ps1", ".plist", ".entitlements", ".json", ".jsonl", ".pbxproj", ".xcscheme", ".xcconfig"}:
                continue
            data = p.read_bytes()
            checked += 1
            if data.startswith(b"\xef\xbb\xbf"):
                errors.append(f"UTF-8 BOM: {p.relative_to(ROOT)}")
            if b"\r\n" in data:
                errors.append(f"CRLF: {p.relative_to(ROOT)}")
            try:
                data.decode("utf-8")
            except UnicodeDecodeError:
                errors.append(f"invalid UTF-8: {p.relative_to(ROOT)}")

    info_path = MAC / "Resources/Info.plist"
    if info_path.exists():
        info = plistlib.loads(info_path.read_bytes())
        if info.get("LSUIElement") is not True:
            errors.append("LSUIElement must be true for the accessory card app")
        if info.get("CFBundleExecutable") not in {"$(EXECUTABLE_NAME)", "${EXECUTABLE_NAME}", "PinCC"}:
            errors.append("unexpected bundle executable")
        for key, expected in {
            "CFBundleIdentifier": "$(PRODUCT_BUNDLE_IDENTIFIER)",
            "CFBundleShortVersionString": "$(MARKETING_VERSION)",
            "CFBundleVersion": "$(CURRENT_PROJECT_VERSION)",
            "LSMinimumSystemVersion": "$(MACOSX_DEPLOYMENT_TARGET)",
        }.items():
            if info.get(key) != expected:
                errors.append(f"{key} must inherit the Xcode build setting")
    ent_path = MAC / "Resources/PinCC.entitlements"
    if ent_path.exists():
        ent = plistlib.loads(ent_path.read_bytes())
        forbidden = ["com.apple.security.app-sandbox", "com.apple.security.cs.disable-library-validation",
                     "com.apple.security.cs.allow-jit", "com.apple.security.cs.allow-unsigned-executable-memory",
                     "com.apple.security.get-task-allow"]
        for key in forbidden:
            if ent.get(key):
                errors.append(f"unnecessary release entitlement: {key}")

    for label, validate in [("Xcode project", validate_project), ("app icons", validate_icons), ("check script", validate_check_script)]:
        try:
            validate()
            checked += 1
        except (ValueError, KeyError, TypeError, IndexError, AttributeError, OSError, ET.ParseError, struct.error) as error:
            errors.append(f"{label}: {error}")

    package_path = MAC / "Package.swift"
    if package_path.exists():
        package = package_path.read_text(encoding="utf-8")
        if re.search(r"\.package\s*\(", package):
            errors.append("third-party Swift package dependency")
        if not package.startswith("// swift-tools-version: 6.0") or "swiftLanguageModes: [.v6]" not in package:
            errors.append("package must use Swift 6 tools and language mode")
        if "platforms: [.macOS(.v13)]" not in package:
            errors.append("package minimum must remain macOS 13")
    fingerprint_path = MAC / "contracts/source-fingerprint.json"
    if fingerprint_path.exists():
        fingerprint = json.loads(fingerprint_path.read_text(encoding="utf-8"))
        sources = fingerprint.get("sources", {})
        if not isinstance(sources, dict) or not sources:
            errors.append("contract fingerprint has no source hashes")
        else:
            stale = []
            for relative, expected in sources.items():
                p = ROOT / relative
                if not p.is_file() or hashlib.sha256(p.read_bytes().replace(b"\r\n", b"\n")).hexdigest() != expected:
                    stale.append(relative)
            if stale:
                message = "stale C# contracts: " + ", ".join(stale)
                if args.allow_stale_contracts:
                    print("WARNING: " + message)
                else:
                    errors.append(message)
    vectors_path = MAC / "contracts/vectors.jsonl"
    if vectors_path.exists():
        if fingerprint_path.exists():
            expected_vectors = fingerprint.get("vectorsSha256")
            if not expected_vectors or hashlib.sha256(vectors_path.read_bytes()).hexdigest() != expected_vectors:
                errors.append("contract vector SHA256 mismatch")
        for line_number, line in enumerate(vectors_path.read_text(encoding="utf-8").splitlines(), 1):
            if line.strip():
                try:
                    json.loads(line)
                except json.JSONDecodeError as error:
                    errors.append(f"vectors.jsonl:{line_number}: {error}")
    if errors:
        for error in errors:
            print("FAIL: " + error, file=sys.stderr)
        return 1
    print(f"Source/package checks passed ({checked} files/checks). Xcode compilation and Mac interaction tests remain separate.")
    return 0

if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (ValueError, KeyError, TypeError, IndexError, AttributeError, OSError, ET.ParseError, struct.error) as error:
        print(f"FAIL: {error}", file=sys.stderr)
        raise SystemExit(1)
