#!/usr/bin/env bash
# 在 WSL 里编译 InStoreLink.dll（借用 Windows 自带的 csc.exe，不需要 .NET SDK）
#
#   bash tools/build_wsl.sh [游戏目录]
#
# 默认游戏目录 /mnt/d/game/maimai/SDEZ1.70/Package（Sinmai.exe 那一层）。
# 原理：WSL 可以直接执行 Windows 的 exe，所以拿着 /mnt/c 下的 csc.exe，
#       把源码（通过 \\wsl.localhost 路径）和游戏里的 DLL 一起编，产物写回工作区。
#
# 在 Windows 侧编译就用 build_instorelink.ps1，两者结果一致。

set -euo pipefail

GAME_WSL="${1:-/mnt/d/game/maimai/SDEZ1.70/Package}"
CSC="/mnt/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe"

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BUILD="$ROOT/build"
mkdir -p "$BUILD"

if [ ! -x "$CSC" ]; then
    echo "找不到 Windows 的 csc.exe：$CSC" >&2
    echo "（这个脚本只能在 WSL 里跑；纯 Linux 环境请用 .NET SDK 自己建工程）" >&2
    exit 1
fi
if [ ! -f "$GAME_WSL/Sinmai_Data/Managed/Assembly-CSharp.dll" ]; then
    echo "游戏目录不对：$GAME_WSL（里面没有 Sinmai_Data/Managed/Assembly-CSharp.dll）" >&2
    exit 1
fi

# WSL 路径 → Windows 路径（\\wsl.localhost\Ubuntu\...）和 /mnt/c、/mnt/d 的盘符形式
to_win() {
    local p="$1"
    if [[ "$p" == /mnt/* ]]; then
        local drive="${p:5:1}"
        printf '%s:\\%s' "${drive^^}" "$(echo "${p:7}" | tr '/' '\\')"
    else
        printf '\\\\wsl.localhost\\Ubuntu%s' "$(echo "$p" | tr '/' '\\')"
    fi
}

GAME_WIN="$(to_win "$GAME_WSL")"
OUT_WIN="$(to_win "$BUILD")\\InStoreLink.dll"

args=(/target:library /nologo /langversion:5 /optimize+ "/out:$OUT_WIN")
for ref in \
    "MelonLoader\\net35\\MelonLoader.dll" \
    "MelonLoader\\net35\\0Harmony.dll" \
    "Sinmai_Data\\Managed\\Assembly-CSharp.dll" \
    "Sinmai_Data\\Managed\\AMDaemon.NET.dll" \
    "Sinmai_Data\\Managed\\UnityEngine.CoreModule.dll" \
    "Sinmai_Data\\Managed\\UnityEngine.dll" \
    "Sinmai_Data\\Managed\\UnityEngine.JSONSerializeModule.dll" \
    "Sinmai_Data\\Managed\\UnityEngine.UI.dll" \
    "Sinmai_Data\\Managed\\Unity.TextMeshPro.dll" ; do
    args+=(/reference:"$GAME_WIN\\$ref")
done

shopt -s nullglob
sources=("$ROOT"/tools/instorelink/*.cs)
if [ ${#sources[@]} -eq 0 ]; then
    echo "tools/instorelink/ 里没有 .cs 文件" >&2
    exit 1
fi
for f in "${sources[@]}"; do
    args+=("$(to_win "$f")")
done

# csc 的输出是 GBK，转成 UTF-8 再显示
set +e
"$CSC" "${args[@]}" 2>&1 | iconv -f GBK -t UTF-8 2>/dev/null
rc=${PIPESTATUS[0]}
set -e

if [ -f "$BUILD/InStoreLink.dll" ] && [ "$rc" -eq 0 ]; then
    echo "BUILD OK -> $BUILD/InStoreLink.dll ($(stat -c%s "$BUILD/InStoreLink.dll") bytes)"
    echo "拷到游戏里：cp '$BUILD/InStoreLink.dll' '$GAME_WSL/Mods/'"
else
    echo "BUILD FAILED" >&2
    exit 1
fi
