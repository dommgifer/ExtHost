#!/usr/bin/env bash
# 在 Linux / macOS 上交叉編譯 Windows 版單一執行檔，輸出到 dist/
# 可指定版本號：./build.sh 0.2.0
set -euo pipefail
version="${1:-}"
root="$(cd "$(dirname "$0")" && pwd)"
dist="$root/dist"

dotnet publish "$root/src/ExtHost/ExtHost.csproj" -c Release -o "$dist" ${version:+"-p:Version=$version"}

cp "$root/README.md" "$dist/"
rm -rf "$dist/samples"
cp -r "$root/samples" "$dist/samples"

echo "完成：$dist/ExtHost.exe"
