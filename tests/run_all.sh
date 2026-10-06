#!/usr/bin/env bash
# 一键跑全部测试（在 WSL 里跑；用到 Windows 自带的 csc.exe，所以不需要 .NET SDK）
#
#   bash tests/run_all.sh [游戏目录]
#
# 十步：
#   1. 编译 InStoreLink.dll（能编过 = 源码和游戏本体的 API 对得上）
#   2. C# 协议单测（序列化 / 解析 / 伪 IP / 配置）
#   3. C# 客户端逻辑单测（建流超时 / 收到 CLOSE），脱离游戏真跑一遍
#   4. Python 协议向量测试（含真实抓包日志的还原）
#   5. Python 端到端测试（起一个真的 instorematchd，跑完开房→建流→传数据→关房）
#   6. Python 异常流程测试（房主先开打 / 目标不在线 / 反复重试 / 超时回收 / 限速）
#   7. 游戏兼容性探针（补丁目标 / 注入字段）
#   8. 补丁参数名检查（Harmony 按名字传参）
#   9. 发行版指纹（client/ 里那两个 dll 是不是真的由当前源码编的）
#  10. 文档一致性（md5 / 字节数 / 补丁条数 / 链接 / 旧名字）

set -uo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
GAME="${1:-/mnt/d/game/maimai/SDEZ1.70/Package}"
CSC="/mnt/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe"
FAILED=0

# 每个测试**实际打印出来的项数**记到这里，最后一步（文档一致性）会拿它对照文档表格。
# 为什么要这么做：文档表格里写着每个测试"（N 项）"，而给测试加了用例之后很容易忘了同步
# —— 这个坑踩过好几次（补丁数 33→34、run_all 8 步→10 步都栽过）。
# 直接截获测试自己的输出（同步，不经过 tee 那种异步中转），最可靠。
mkdir -p "$ROOT/build"
COUNTS="$ROOT/build/test-counts.txt"
: > "$COUNTS"

# 跑一个测试并把它的项数记下来：run_step <key> <命令...>
run_step() {
    local key="$1"; shift
    local out rc n
    out="$("$@" 2>&1)"; rc=$?
    printf '%s\n' "$out"
    n="$(printf '%s\n' "$out" \
         | grep -aoE '（[0-9]+ 项）|通过 [0-9]+ 项' | tail -1 | grep -oE '[0-9]+')"
    if [ -n "$n" ]; then
        echo "$key $n" >> "$COUNTS"
    else
        echo "$key ?" >> "$COUNTS"          # 没抓到项数（比如测试直接崩了）
    fi
    return $rc
}

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
    run_step protocol "$ROOT/build/ProtocolTests.exe" || FAILED=1
else
    echo "  · 跳过（没有 csc.exe）"
fi

echo
echo "########## 3. C# 客户端逻辑单测（建流超时 / CLOSE 处理）"
if [ -x "$CSC" ]; then
    to_win() {
        local p="$1"
        if [[ "$p" == /mnt/* ]]; then local d="${p:5:1}"; printf '%s:\\%s' "${d^^}" "$(echo "${p:7}" | tr '/' '\\')"
        else printf '\\\\wsl.localhost\\Ubuntu%s' "$(echo "$p" | tr '/' '\\')"; fi
    }
    rm -f "$ROOT/build/ClientTests.exe"     # 编译失败时别拿上一次的 exe 报绿
    "$CSC" /target:exe /nologo /langversion:5 \
        "/out:$(to_win "$ROOT/build")\\ClientTests.exe" \
        "$(to_win "$ROOT/tools/instorelink/LinkProtocol.cs")" \
        "$(to_win "$ROOT/tools/instorelink/LinkConfig.cs")" \
        "$(to_win "$ROOT/tools/instorelink/LinkLog.cs")" \
        "$(to_win "$ROOT/tools/instorelink/LinkClient.cs")" \
        "$(to_win "$ROOT/tools/instorelink/LinkSocket.cs")" \
        "$(to_win "$ROOT/tests/ClientTests.cs")" 2>&1 | iconv -f GBK -t UTF-8 2>/dev/null
    if [ -f "$ROOT/build/ClientTests.exe" ]; then
        chmod +x "$ROOT/build/ClientTests.exe"
        run_step client "$ROOT/build/ClientTests.exe" || FAILED=1
    else
        echo "客户端逻辑单测没编出来（见上面 csc 的输出）" >&2
        FAILED=1
    fi
else
    echo "  · 跳过（没有 csc.exe）"
fi

echo
echo "########## 4. Python 协议向量测试"
# 有真实抓包日志的话（设 MAIMAI_LOGS 指向那个目录），这一段会额外验证日志里的报文
run_step vectors env MAIMAI_LOGS="${MAIMAI_LOGS:-}" python3 "$ROOT/tests/py/test_vectors.py" || FAILED=1

echo
echo "########## 5. Python 端到端测试"
run_step e2e python3 "$ROOT/tests/py/test_e2e.py" || FAILED=1

echo
echo "########## 6. Python 异常流程测试（房主先开打 / 目标不在线 / 反复重试 / 超时回收 / 限速）"
run_step edge python3 "$ROOT/tests/py/test_edge.py" || FAILED=1

echo
echo "########## 7. 游戏兼容性探针（补丁目标 / 注入字段）"
if [ -d "$GAME/Sinmai_Data/Managed" ]; then
    run_step probe bash "$ROOT/tests/run_probe.sh" "$GAME" || FAILED=1
else
    echo "  · 跳过（找不到游戏目录 $GAME）"
fi

echo
echo "########## 8. 补丁参数名检查（Harmony 按名字传参）"
if [ -d "$GAME/Sinmai_Data/Managed" ]; then
    bash "$ROOT/tests/run_param_check.sh" "$GAME" || FAILED=1
else
    echo "  · 跳过（找不到游戏目录 $GAME）"
fi

echo
echo "########## 9. 发行版指纹（client/ 里的 dll vs 当前源码）"
bash "$ROOT/tests/run_fingerprint.sh" "$GAME" || FAILED=1

echo
echo "########## 10. 文档一致性（md5 / 字节数 / 补丁条数 / 链接 / 旧名字）"
python3 "$ROOT/tests/py/test_docs.py" || FAILED=1

echo
if [ "$FAILED" -eq 0 ]; then
    echo "########## 全部测试通过"
else
    echo "########## 有测试失败（见上面输出）" >&2
fi
exit "$FAILED"
