#!/usr/bin/env sh
# Publishes a self-contained altacli for Linux and packs it for the Harbor adapter (locked_codealta.py).
# The sandbox images have no .NET: the runtime is in the package. Invariant globalization: the images may lack libicu.
# Usage: build-altacli.sh [runtime] [configuration]   (default: linux-x64 Release)
set -eu
runtime="${1:-linux-x64}"
configuration="${2:-Release}"
here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../.." && pwd)"
dist="$here/dist"
publish="$dist/altacli-$runtime"
tarball="$dist/altacli-$runtime.tar.gz"

rm -rf "$publish" "$tarball"
mkdir -p "$dist"
dotnet publish "$root/src/CodeAlta.Cli/CodeAlta.Cli.csproj" -c "$configuration" -r "$runtime" --self-contained \
  -p:InvariantGlobalization=true -o "$publish"

# The native SQLite of SQLitePCLRaw 2.1.13 needs glibc 2.34; many task images have an older one (Debian 11: 2.31).
# The one of 2.1.11 (SQLite 3.49.1) needs glibc 2.28 and works with the managed provider of 2.1.13.
if [ "$runtime" = "linux-x64" ]; then
  sqlite_version=2.1.11
  sqlite_sha256=965400c23fde16a19d000c1c6062af577f048410f85253a793f569d5520daaad
  sqlite_dir="$dist/e_sqlite3-$sqlite_version"
  rm -rf "$sqlite_dir" && mkdir -p "$sqlite_dir"
  curl -fsSL -o "$sqlite_dir/package.zip" "https://www.nuget.org/api/v2/package/SQLitePCLRaw.lib.e_sqlite3/$sqlite_version"
  unzip -q -o "$sqlite_dir/package.zip" runtimes/linux-x64/native/libe_sqlite3.so -d "$sqlite_dir"
  echo "$sqlite_sha256  $sqlite_dir/runtimes/linux-x64/native/libe_sqlite3.so" | sha256sum -c - >/dev/null
  cp "$sqlite_dir/runtimes/linux-x64/native/libe_sqlite3.so" "$publish/libe_sqlite3.so"
  rm -rf "$sqlite_dir"
fi
tar -czf "$tarball" -C "$publish" .
(cd "$dist" && sha256sum "altacli-$runtime.tar.gz" > "altacli-$runtime.tar.gz.sha256")
echo "$tarball $(cut -d' ' -f1 "$tarball.sha256")"
