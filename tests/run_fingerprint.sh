#!/usr/bin/env bash
# 检查「仓库里发行的那两个 dll」是不是真的由当前源码编出来的。
#
# 为什么单列一步：csc 的输出不可复现（每次编译 MVID / 时间戳都变，md5 必然不同），
# 所以「源码 ↔ 发行版」没法用 md5 比。这里重新编一份，再用 tools/fingerprint.cs
# 算"与编译随机性无关"的指纹对比 —— 对不上就说明发行版落后于源码，
# 用户装到的是旧逻辑（这个坑真踩过一次：client/InStoreLink.dll 少了 LinkConfig 的 BOM 兼容）。
#
#   bash tests/run_fingerprint.sh [游戏目录]

set -uo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
GAME="${1:-/mnt/d/game/maimai/SDEZ1.70/Package}"
CSC="/mnt/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe"
CECIL="$GAME/MelonLoader/net35/Mono.Cecil.dll"

if [ ! -x "$CSC" ] || [ ! -f "$CECIL" ]; then
    echo "  · 跳过（需要 Windows 的 csc.exe 和游戏自带的 Mono.Cecil.dll）"
    exit 0
fi
if [ ! -d "$GAME/Sinmai_Data/Managed" ]; then
    echo "  · 跳过（游戏目录不对：$GAME）"
    exit 0
fi

to_win() {
    local p="$1"
    if [[ "$p" == /mnt/* ]]; then local d="${p:5:1}"; printf '%s:\\%s' "${d^^}" "$(echo "${p:7}" | tr '/' '\\')"
    else printf '\\\\wsl.localhost\\Ubuntu%s' "$(echo "$p" | tr '/' '\\')"; fi
}

mkdir -p "$ROOT/build"
cp -f "$CECIL" "$ROOT/build/Mono.Cecil.dll"

# 1) 编指纹工具
"$CSC" /target:exe /nologo /langversion:5 \
    "/r:$(to_win "$CECIL")" "/out:$(to_win "$ROOT/build")\\fingerprint.exe" \
    "$(to_win "$ROOT/tools/fingerprint.cs")" 2>&1 | iconv -f GBK -t UTF-8 2>/dev/null
chmod +x "$ROOT/build/fingerprint.exe"
if [ ! -f "$ROOT/build/fingerprint.exe" ]; then
    echo "指纹工具编译失败" >&2
    exit 1
fi

# 2) 保证 build/ 里两份都是刚编的（InStoreLink 由 run_all.sh 的第 1 步产出，
#    但单独跑这个脚本时可能还没有，缺了就补一下）
if [ ! -f "$ROOT/build/InStoreLink.dll" ]; then
    bash "$ROOT/tools/build_wsl.sh" "$GAME" || exit 1
fi
# 每次都重编：InStoreMatch 是单文件、零点几秒，省掉"build/ 里那份是不是过期了"的判断
IM_ARGS=(/target:library /nologo /optimize+ "/out:$(to_win "$ROOT/build")\\InStoreMatch.dll")
for ref in \
    "MelonLoader\\net35\\MelonLoader.dll" \
    "MelonLoader\\net35\\0Harmony.dll" \
    "Sinmai_Data\\Managed\\Assembly-CSharp.dll" \
    "Sinmai_Data\\Managed\\AMDaemon.NET.dll" \
    "Sinmai_Data\\Managed\\UnityEngine.CoreModule.dll" \
    "Sinmai_Data\\Managed\\UnityEngine.dll" ; do
    IM_ARGS+=(/reference:"$(to_win "$GAME")\\$ref")
done
IM_ARGS+=("$(to_win "$ROOT/tools/InStoreMatch.cs")")
"$CSC" "${IM_ARGS[@]}" 2>&1 | iconv -f GBK -t UTF-8 2>/dev/null

# 3) 逐对比较
RC=0
compare() {
    local shipped="$1" fresh="$2"
    if [ ! -f "$shipped" ]; then
        echo "  · 仓库里没有 $shipped，跳过"
        return 0
    fi
    if [ ! -f "$fresh" ]; then
        echo "  ✗ 编不出 $fresh" >&2
        RC=1
        return 0
    fi
    echo "--- $(basename "$shipped")：发行版 vs 当前源码"
    "$ROOT/build/fingerprint.exe" "$(to_win "$shipped")" "$(to_win "$fresh")" || RC=1
}

compare "$ROOT/client/InStoreLink.dll"  "$ROOT/build/InStoreLink.dll"
echo
compare "$ROOT/client/InStoreMatch.dll" "$ROOT/build/InStoreMatch.dll"

if [ "$RC" -ne 0 ]; then
    echo
    echo "!! 发行版和当前源码编出来的不一致 —— 用户装到的不是你现在这份逻辑。" >&2
    echo "   修：把 build/ 里的新产物拷进 client/（InStoreLink.dll / InStoreMatch.dll）并提交。" >&2
fi
exit "$RC"
