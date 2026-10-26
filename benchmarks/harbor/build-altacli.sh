#!/usr/bin/env sh
# Publishes glibc and musl self-contained altacli builds and packs them for the Harbor adapter (locked_codealta.py).
# The sandbox images have no .NET: both runtimes are in the package. Invariant globalization: images may lack libicu.
# Usage: build-altacli.sh [architecture-runtime] [configuration]   (default: linux-x64 Release)
set -eu
runtime="${1:-linux-x64}"
configuration="${2:-Release}"
[ "$runtime" = linux-x64 ] || { echo "Only the x64 dual-runtime package is supported; got $runtime" >&2; exit 2; }
here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../.." && pwd)"
dist="$here/dist"
package_root="$dist/altacli-$runtime"
tarball="$dist/altacli-$runtime.tar.gz"

rm -rf "$package_root" "$tarball"
mkdir -p "$dist"
for target_runtime in linux-x64 linux-musl-x64; do
  dotnet publish "$root/src/CodeAlta.Cli/CodeAlta.Cli.csproj" -c "$configuration" -r "$target_runtime" --self-contained \
    -p:InvariantGlobalization=true -o "$package_root/$target_runtime"
done

# SQLitePCLRaw 2.1.13 needs glibc 2.34. Pin the glibc and musl libraries of 2.1.11 (SQLite 3.49.1);
# its glibc build needs 2.28, and both work with the managed provider of 2.1.13.
sqlite_version=2.1.11
sqlite_dir="$dist/e_sqlite3-$sqlite_version"
rm -rf "$sqlite_dir" && mkdir -p "$sqlite_dir"
curl -fsSL -o "$sqlite_dir/package.zip" "https://www.nuget.org/api/v2/package/SQLitePCLRaw.lib.e_sqlite3/$sqlite_version"
unzip -q -o "$sqlite_dir/package.zip" \
  runtimes/linux-x64/native/libe_sqlite3.so \
  runtimes/linux-musl-x64/native/libe_sqlite3.so \
  -d "$sqlite_dir"
echo "965400c23fde16a19d000c1c6062af577f048410f85253a793f569d5520daaad  $sqlite_dir/runtimes/linux-x64/native/libe_sqlite3.so" | sha256sum -c - >/dev/null
echo "24868846aa49e7d030908d44051a7ecec0e1315524e92bc21a5f6dda816790f6  $sqlite_dir/runtimes/linux-musl-x64/native/libe_sqlite3.so" | sha256sum -c - >/dev/null
cp "$sqlite_dir/runtimes/linux-x64/native/libe_sqlite3.so" "$package_root/linux-x64/libe_sqlite3.so"
cp "$sqlite_dir/runtimes/linux-musl-x64/native/libe_sqlite3.so" "$package_root/linux-musl-x64/libe_sqlite3.so"
rm -rf "$sqlite_dir"

tar -czf "$tarball" -C "$package_root" .
(cd "$dist" && sha256sum "altacli-$runtime.tar.gz" > "altacli-$runtime.tar.gz.sha256")
echo "$tarball $(cut -d' ' -f1 "$tarball.sha256")"
