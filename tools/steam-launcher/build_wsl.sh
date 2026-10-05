#!/usr/bin/env bash
# 用 zig 交叉编译 MaimaiSteam.exe（不需要装 Visual Studio）
#
#   ZIG=/path/to/zig bash tools/steam-launcher/build_wsl.sh
#
# 没给 ZIG 就依次找 /tmp/zig/zig、PATH 里的 zig。
# zig 下载：https://ziglang.org/download/ （linux x86_64 包，解压即用）
#
# 产物：client/MaimaiSteam.exe（GUI 子系统、静态链接、不依赖任何运行库）

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SRC="$ROOT/tools/steam-launcher/launcher.cpp"
OUT="$ROOT/client/MaimaiSteam.exe"

ZIG="${ZIG:-}"
if [ -z "$ZIG" ]; then
    if [ -x /tmp/zig/zig ]; then ZIG=/tmp/zig/zig
    elif command -v zig >/dev/null 2>&1; then ZIG="$(command -v zig)"
    fi
fi
if [ -z "$ZIG" ] || [ ! -x "$ZIG" ]; then
    echo "找不到 zig。装一个（解压即用）或指定 ZIG=/path/to/zig" >&2
    echo "  curl -L -o /tmp/zig.tar.xz https://ziglang.org/download/<版本>/zig-x86_64-linux-<版本>.tar.xz" >&2
    echo "  mkdir -p /tmp/zig && tar -xJf /tmp/zig.tar.xz -C /tmp/zig --strip-components=1" >&2
    exit 1
fi

echo "用 $("$ZIG" version) 交叉编译 -> $OUT"
"$ZIG" c++ -target x86_64-windows-gnu -O2 -municode -Wl,--subsystem,windows -static -o "$OUT" "$SRC"

# zig 会顺手生成一个 pdb，仓库里不需要
rm -f "${OUT%.exe}.pdb"

ls -la "$OUT"
echo "BUILD OK"
