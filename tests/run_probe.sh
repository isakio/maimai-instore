#!/usr/bin/env bash
# 跑「游戏兼容性探针」：检查我们每个补丁的目标方法/注入字段在这个游戏版本里对不对得上。
#
#   bash tests/run_probe.sh [游戏目录]
#
# 原理：探针 exe 必须和游戏 DLL 同目录才能用只反射模式加载 Assembly-CSharp.dll，
#       所以这里临时拷进 <游戏>\Sinmai_Data\Managed\ 执行，跑完立刻删掉（不污染游戏目录）。
#
# 什么时候跑：游戏更新之后、或者日志里出现"补丁有问题"的时候。

set -uo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
GAME="${1:-/mnt/d/game/maimai/SDEZ1.70/Package}"
CSC="/mnt/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe"

if [ ! -x "$CSC" ]; then
    echo "找不到 Windows 的 csc.exe（这个脚本只能在 WSL 里跑）" >&2
    exit 1
fi
if [ ! -d "$GAME/Sinmai_Data/Managed" ]; then
    echo "游戏目录不对：$GAME" >&2
    exit 1
fi

to_win() {
    local p="$1"
    if [[ "$p" == /mnt/* ]]; then local d="${p:5:1}"; printf '%s:\\%s' "${d^^}" "$(echo "${p:7}" | tr '/' '\\')"
    else printf '\\\\wsl.localhost\\Ubuntu%s' "$(echo "$p" | tr '/' '\\')"; fi
}

mkdir -p "$ROOT/build"
"$CSC" /target:exe /nologo /langversion:5 \
    "/out:$(to_win "$ROOT/build")\\GameCompatProbe.exe" \
    "$(to_win "$ROOT/tests/GameCompatProbe.cs")" 2>&1 | iconv -f GBK -t UTF-8 2>/dev/null

TARGET="$GAME/Sinmai_Data/Managed/GameCompatProbe.exe"
cp "$ROOT/build/GameCompatProbe.exe" "$TARGET"
chmod +x "$TARGET"
set +e
( cd "$GAME/Sinmai_Data/Managed" && ./GameCompatProbe.exe )
rc=$?
set -e
rm -f "$TARGET"

exit "$rc"
