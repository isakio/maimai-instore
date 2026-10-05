#!/usr/bin/env bash
# 补丁参数名检查
#
# 为什么单列一个脚本：Harmony 给补丁方法传参是**按参数名**找的（__instance / __result /
# ___字段 这些特殊名字除外）。名字写错编译期完全没提示，跑起来才抛
# "IL Compile Error (unknown location)"，真踩过一次（nfSocket 写成了 socket）。
#
# 顺带查另外两类编译期同样看不出来的错：
#   · 补丁忘了带 [HarmonyPrefix] / [HarmonyPostfix]（Harmony 直接拒绝这条补丁）
#   · `___字段` 注入的字段在目标类型（含父类）里不存在（注入失败 = 这条补丁等于没打）
#
#   bash tests/run_param_check.sh [游戏目录]

set -uo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
GAME="${1:-/mnt/d/game/maimai/SDEZ1.70/Package}"
CSC="/mnt/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe"
CECIL_WSL="$GAME/MelonLoader/net35/Mono.Cecil.dll"

if [ ! -f "$CECIL_WSL" ]; then
    echo "找不到 $CECIL_WSL（游戏目录不对？）" >&2
    exit 1
fi
# 要查哪些程序集：
#   build/  —— 刚编出来的，源码一改就能第一时间发现回归
#   client/ —— 真正发给用户的那一份，能发现"源码改了但忘了重编发行版"
# 两个都在就都查；只要求至少有一个，方便单独跑源码那份。
DLLS=()
[ -f "$ROOT/build/InStoreLink.dll" ] && DLLS+=("$ROOT/build/InStoreLink.dll")
[ -f "$ROOT/client/InStoreLink.dll" ] && DLLS+=("$ROOT/client/InStoreLink.dll")
if [ ${#DLLS[@]} -eq 0 ]; then
    echo "build/ 和 client/ 里都没有 InStoreLink.dll，先跑 tools/build_wsl.sh" >&2
    exit 1
fi

to_win() {
    local p="$1"
    if [[ "$p" == /mnt/* ]]; then local d="${p:5:1}"; printf '%s:\\%s' "${d^^}" "$(echo "${p:7}" | tr '/' '\\')"
    else printf '\\\\wsl.localhost\\Ubuntu%s' "$(echo "$p" | tr '/' '\\')"; fi
}

# 检查器 exe 要和 Mono.Cecil.dll 放一起才跑得起来
mkdir -p "$ROOT/build"
cp -f "$CECIL_WSL" "$ROOT/build/Mono.Cecil.dll"
"$CSC" /target:exe /nologo \
    "/r:$(to_win "$CECIL_WSL")" "/out:$(to_win "$ROOT/build")\\check_patch_params.exe" \
    "$(to_win "$ROOT/tools/check_patch_params.cs")" 2>&1 | iconv -f GBK -t UTF-8 2>/dev/null
chmod +x "$ROOT/build/check_patch_params.exe"

RC=0
for dll in "${DLLS[@]}"; do
    echo
    echo "--- 检查 ${dll#"$ROOT"/}"
    "$ROOT/build/check_patch_params.exe" \
        "$(to_win "$dll")" \
        "$(to_win "$GAME/Sinmai_Data/Managed/Assembly-CSharp.dll")" || RC=1
done
exit "$RC"
