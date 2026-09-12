#!/usr/bin/env bash
# Build the Java "launcher part" jars FlintLauncher needs at launch time:
#
#   NewLaunch.jar        <- libraries/launcher/org/prismlauncher/**   (the standard launcher)
#   NewLaunchLegacy.jar  <- libraries/launcher/legacy/**              (the legacy/applet variant,
#                          compiled against NewLaunch.jar like the CMake INCLUDE_JARS)
#   JavaCheck.jar        <- libraries/javacheck/JavaCheck.java        (the java checker the
#                          CheckJava step runs to probe the JVM)
#
# This mirrors libraries/launcher/CMakeLists.txt and libraries/javacheck/CMakeLists.txt
# (add_jar + CMAKE_JAVA_JAR_ENTRY_POINT org.prismlauncher.EntryPoint / JavaCheck). The CMake
# file targets Java 7, which current JDKs no longer accept, so the sources are compiled with
# --release 8 instead (the java.applet API the legacy frame needs is still in the ct.sym
# historical data for that target).
#
# The produced jars are local build outputs and are NOT committed; they are picked up at
# runtime by Launch.LauncherJars.GetJarPath / JavaUtils.GetJavaCheckPath, which search
# <launcher root>/jars (among others). Pass the output directory as the first argument
# (default: <repo>/build/jars); for use with the test suite copy the jars (or point
# JavaUtils.LauncherRootPath) accordingly.
#
# Usage: docs/tools/newlaunch-build.sh [output-dir]
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
out_dir="${1:-$repo_root/build/jars}"

command -v javac >/dev/null 2>&1 || { echo "error: javac not found on PATH (a JDK >= 8 is required)" >&2; exit 1; }
command -v jar >/dev/null 2>&1 || { echo "error: jar not found on PATH (a JDK >= 8 is required)" >&2; exit 1; }

launcher_src="$repo_root/libraries/launcher"
javacheck_src="$repo_root/libraries/javacheck"

work_dir="$(mktemp -d)"
trap 'rm -rf "$work_dir"' EXIT
classes_dir="$work_dir/classes"
mkdir -p "$classes_dir" "$out_dir"

# libraries/launcher CMakeLists.txt SRC list (NewLaunch.jar)
main_sources=(
    org/prismlauncher/EntryPoint.java
    org/prismlauncher/launcher/Launcher.java
    org/prismlauncher/launcher/impl/AbstractLauncher.java
    org/prismlauncher/launcher/impl/StandardLauncher.java
    org/prismlauncher/exception/ParameterNotFoundException.java
    org/prismlauncher/exception/ParseException.java
    org/prismlauncher/utils/Parameters.java
    org/prismlauncher/utils/ReflectionUtils.java
    org/prismlauncher/utils/logging/Level.java
    org/prismlauncher/utils/logging/Log.java
    org/prismlauncher/legacy/LegacyProxy.java
)

# libraries/launcher CMakeLists.txt LEGACY_SRC list (NewLaunchLegacy.jar)
legacy_sources=(
    legacy/org/prismlauncher/legacy/LegacyFrame.java
    legacy/org/prismlauncher/legacy/LegacyLauncher.java
    legacy/org/prismlauncher/legacy/fix/online/Handler.java
    legacy/org/prismlauncher/legacy/fix/online/OnlineFixes.java
    legacy/org/prismlauncher/legacy/fix/online/OnlineModeFix.java
    legacy/org/prismlauncher/legacy/fix/online/SkinFix.java
    legacy/org/prismlauncher/legacy/utils/Base64.java
    legacy/org/prismlauncher/legacy/utils/api/MojangApi.java
    legacy/org/prismlauncher/legacy/utils/api/Texture.java
    legacy/org/prismlauncher/legacy/utils/json/JsonParseException.java
    legacy/org/prismlauncher/legacy/utils/json/JsonParser.java
    legacy/org/prismlauncher/legacy/utils/url/ByteArrayUrlConnection.java
    legacy/org/prismlauncher/legacy/utils/url/UrlUtils.java
    legacy/net/minecraft/Launcher.java
    legacy/org/prismlauncher/legacy/LegacyProxy.java
)

echo "==> Compiling NewLaunch.jar sources"
(cd "$launcher_src" && javac --release 8 -d "$classes_dir" "${main_sources[@]}")

echo "==> Packaging NewLaunch.jar"
jar --create --file "$out_dir/NewLaunch.jar" --main-class org.prismlauncher.EntryPoint -C "$classes_dir" .

echo "==> Compiling NewLaunchLegacy.jar sources (against NewLaunch.jar)"
rm -rf "$classes_dir"
mkdir -p "$classes_dir"
(cd "$launcher_src" && javac --release 8 -cp "$out_dir/NewLaunch.jar" -d "$classes_dir" "${legacy_sources[@]}")

echo "==> Packaging NewLaunchLegacy.jar"
# UseJava's INCLUDE_JARS(NewLaunch) adds NewLaunch.jar to the legacy manifest Class-Path
jar --create --file "$out_dir/NewLaunchLegacy.jar" --main-class org.prismlauncher.EntryPoint \
    --manifest <(printf 'Class-Path: NewLaunch.jar\n') -C "$classes_dir" .

echo "==> Compiling JavaCheck.jar sources"
rm -rf "$classes_dir"
mkdir -p "$classes_dir"
javac --release 8 -Xlint:deprecation -Xlint:unchecked -d "$classes_dir" "$javacheck_src/JavaCheck.java"

echo "==> Packaging JavaCheck.jar"
jar --create --file "$out_dir/JavaCheck.jar" --main-class JavaCheck -C "$classes_dir" .

echo "==> Done. Jars in $out_dir:"
ls -l "$out_dir" | grep -E '\.jar$'
