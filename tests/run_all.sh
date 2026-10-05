#!/usr/bin/env bash
# 一键跑全部测试（在 WSL 里跑；用到 Windows 自带的 csc.exe，所以不需要 .NET SDK）
#
#   bash tests/run_all.sh [游戏目录]
#
# 七步：
#   1. 编译 InStoreLink.dll（能编过 = 源码和游戏本体的 API 对得上）
#   2. C# 协议单测（序列化 / 解析 / 伪 IP / 配置）
#   3. Python 协议向量测试（含真实抓包日志的还原）
#   4. Python 端到端测试（起一个真的 instorematchd，跑完开房→建流→传数据→关房）
#   5. 游戏兼容性探针（补丁目标 / 注入字段）
#   6. 补丁参数名检查（Harmony 按名字传参）
#   7. 发行版指纹（client/ 里那两个 dll 是不是真的由当前源码编的）

set -uo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
GAME="${1:-/mnt/d/game/maimai/SDEZ1.70/Package}"
CSC="/mnt/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe"
FAILED=0

echo "########## 1. 编译 InStoreLink.dll"
if [ -x "$CSC" ]; then
    bash "$ROOT/tools/build_wsl.sh" "$GAME" || { echo "编译失败，后面没法验证"; exit 1; }
else
    echo "  · 没找到 Windows 的 csc.exe，跳过编译（纯 Linux 环境请用 .NET SDK）"
fi

echo
echo "########## 2. C# 协议单测"
if [ -x "$CSC" ]; then
    to_win() {
        local p="$1"
        if [[ "$p" == /mnt/* ]]; then local d="${p:5:1}"; printf '%s:\\%s' "${d^^}" "$(echo "${p:7}" | tr '/' '\\')"
        else printf '\\\\wsl.localhost\\Ubuntu%s' "$(echo "$p" | tr '/' '\\')"; fi
    }
    "$CSC" /target:exe /nologo /langversion:5 \
        "/out:$(to_win "$ROOT/build")\\ProtocolTests.exe" \
        "$(to_win "$ROOT/tools/instorelink/LinkProtocol.cs")" \
        "$(to_win "$ROOT/tools/instorelink/LinkConfig.cs")" \
        "$(to_win "$ROOT/tests/ProtocolTests.cs")" 2>&1 | iconv -f GBK -t UTF-8 2>/dev/null
    chmod +x "$ROOT/build/ProtocolTests.exe"
    "$ROOT/build/ProtocolTests.exe" || FAILED=1
else
    echo "  · 跳过（没有 csc.exe）"
fi

echo
echo "########## 3. Python 协议向量测试"
# 有真实抓包日志的话（设 MAIMAI_LOGS 指向那个目录），这一段会额外验证日志里的报文
MAIMAI_LOGS="${MAIMAI_LOGS:-}" python3 "$ROOT/tests/py/test_vectors.py" || FAILED=1

echo
echo "########## 4. Python 端到端测试"
python3 "$ROOT/tests/py/test_e2e.py" || FAILED=1

echo
echo "########## 5. 游戏兼容性探针（补丁目标 / 注入字段）"
if [ -d "$GAME/Sinmai_Data/Managed" ]; then
    bash "$ROOT/tests/run_probe.sh" "$GAME" || FAILED=1
else
    echo "  · 跳过（找不到游戏目录 $GAME）"
fi

echo
echo "########## 6. 补丁参数名检查（Harmony 按名字传参）"
if [ -d "$GAME/Sinmai_Data/Managed" ]; then
    bash "$ROOT/tests/run_param_check.sh" "$GAME" || FAILED=1
else
    echo "  · 跳过（找不到游戏目录 $GAME）"
fi

echo
echo "########## 7. 发行版指纹（client/ 里的 dll vs 当前源码）"
bash "$ROOT/tests/run_fingerprint.sh" "$GAME" || FAILED=1

echo
if [ "$FAILED" -eq 0 ]; then
    echo "########## 全部测试通过"
else
    echo "########## 有测试失败（见上面输出）" >&2
fi
exit "$FAILED"
