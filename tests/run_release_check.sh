#!/usr/bin/env bash
# 检查「GitHub 上最新 Release 挂的那两个 dll」是不是和仓库 client/ 里的实物一致。
#
#   bash tests/run_release_check.sh            # 检查 latest
#   bash tests/run_release_check.sh v3.0       # 检查指定 tag
#
# 为什么单列一步：这个坑我们已经踩过一次 —— client/ 重编并提交了，但 Release 附件
# 还是旧的那份，而 README 写着"最新 Release 也附了"，照 README 下载的人拿到的是旧逻辑。
# （更早一次是附件里的 md5 已经写进 release 说明，也一起过期了。）
#
# 注意：**不要用下载来比**。GitHub 的 releases/download 链接走 CDN，刚替换完附件时
# 可能还命中旧缓存（实测过：服务端 digest 已经是新的，下载回来还是旧的，几分钟后才对上）。
# 所以这里拿 API 报的 digest（服务端真值）比。
#
# 需要 gh（已登录）+ 能联网；没有的话打印"跳过"并以 0 退出，
# 免得纯离线环境跑不了 —— 它不在 run_all.sh 那 10 步里，按需单独跑。

set -uo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TAG="${1:-latest}"

if ! command -v gh >/dev/null 2>&1; then
    echo "  · 跳过（没装 gh）"
    exit 0
fi
if ! command -v sha256sum >/dev/null 2>&1; then
    echo "  · 跳过（没有 sha256sum）"
    exit 0
fi

REPO="$(cd "$ROOT" && gh repo view --json nameWithOwner -q .nameWithOwner 2>/dev/null)"
if [ -z "$REPO" ]; then
    echo "  · 跳过（gh 没登录 / 拿不到仓库名）"
    exit 0
fi

ENDPOINT="repos/$REPO/releases/tags/$TAG"
if [ "$TAG" = "latest" ]; then
    ENDPOINT="repos/$REPO/releases/latest"
fi

LIST="$(gh api "$ENDPOINT" -q '.tag_name, (.assets[] | "\(.name)\t\(.size)\t\(.digest)")' 2>/dev/null)"
if [ -z "$LIST" ]; then
    echo "  · 跳过（取不到 $TAG 的信息 —— 没联网 / 还没发布？）"
    exit 0
fi

echo "== Release 附件一致性（$REPO $TAG）=="
TAKEN_TAG="$(printf '%s\n' "$LIST" | head -1)"
echo "tag: $TAKEN_TAG"

RC=0
for name in InStoreLink.dll InStoreMatch.dll; do
    local_file="$ROOT/client/$name"
    if [ ! -f "$local_file" ]; then
        echo "  ✗ client/$name 不存在" >&2
        RC=1
        continue
    fi
    want_size="$(stat -c%s "$local_file")"
    want_digest="sha256:$(sha256sum "$local_file" | cut -d' ' -f1)"

    # 附件行（跳过第一行的 tag）
    line="$(printf '%s\n' "$LIST" | sed -n "2,\$p" | awk -F'\t' -v n="$name" '$1==n')"
    if [ -z "$line" ]; then
        echo "  ✗ Release 里没有附件 $name" >&2
        RC=1
        continue
    fi
    got_size="$(printf '%s' "$line" | cut -f2)"
    got_digest="$(printf '%s' "$line" | cut -f3)"

    if [ "$got_size" = "$want_size" ] && [ "$got_digest" = "$want_digest" ]; then
        echo "  ✓ $name：$got_size 字节 / ${got_digest#sha256:}"
    else
        echo "  ✗ $name 和 client/ 里的不一致" >&2
        echo "      Release: $got_size 字节 / $got_digest" >&2
        echo "      本地:    $want_size 字节 / $want_digest" >&2
        RC=1
    fi
done

if [ "$RC" -ne 0 ]; then
    echo
    echo "!! 把 client/ 里那两个 dll 重新挂上去（并同步 release 说明里的 md5）：" >&2
    echo "   gh release upload $TAG client/InStoreLink.dll client/InStoreMatch.dll --clobber" >&2
fi
exit "$RC"
