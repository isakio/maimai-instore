#!/usr/bin/env bash
# instorematchd 一键安装（systemd），适用于 Debian / Ubuntu 等常见 Linux。
#
#   sudo bash install.sh <你的域名或公网IP> [大厅端口] [中继端口]
#
# 例：sudo bash install.sh 203.0.113.10
#     sudo bash install.sh maimai.example.com 20100 20101
#
# 装完记得在防火墙 / 云厂商安全组放行这两个 TCP 端口。
#
# 幂等：重跑即升级（会 restart，所以新拷贝的代码一定生效）。
set -euo pipefail

HOST_OVERRIDE="${1:-}"
LOBBY_PORT="${2:-20100}"
RELAY_PORT="${3:-20101}"

if [ -z "$HOST_OVERRIDE" ]; then
    echo "用法: sudo bash install.sh <你的域名或公网IP> [大厅端口] [中继端口]" >&2
    exit 1
fi
if [ "$(id -u)" != "0" ]; then
    echo "请用 root 运行（sudo bash install.sh ...）" >&2
    exit 1
fi

SRC="$(cd "$(dirname "$0")" && pwd)"
DIR=/opt/instorematchd
PY=/usr/bin/python3

# instorematchd 用了 `str | None` 这类 3.10+ 语法
if ! "$PY" -c 'import sys; sys.exit(0 if sys.version_info >= (3, 10) else 1)' 2>/dev/null; then
    echo "需要 Python 3.10 或更高版本（当前: $("$PY" -V 2>&1 || echo 未安装)）" >&2
    exit 1
fi

# 端口预检：被"别人"占着的话新服务只会一直 auto-restart，这里直接说清楚。
# 注意：升级（重跑本脚本）时 20100/20101 正是 instorematchd 自己在占，必须跳过检查，
# 否则「幂等重跑」就废了。
# （另外别写成 `... | grep -q`：grep -q 匹配到就退出，上游进程收到 SIGPIPE(141)，
#   在 set -o pipefail 下整条管道会被判成失败，条件永远不成立 —— 踩过。）
if systemctl is-active --quiet instorematchd 2>/dev/null; then
    echo "    检测到 instorematchd 正在运行，按升级处理（端口检查跳过）"
else
    for p in "$LOBBY_PORT" "$RELAY_PORT"; do
        holder="$(ss -tlnpH 2>/dev/null | awk -v pat=":$p\$" '$4 ~ pat' | tr '\n' ' ')"
        if [ -n "$holder" ]; then
            echo "!! TCP $p 已经被占用：" >&2
            echo "   $holder" >&2
            echo "   先停掉占用它的服务或进程（比如之前用别的名字部署的同一套服务：systemctl disable --now <那个单元>），再重跑本脚本。" >&2
            exit 1
        fi
    done
fi

echo "==> 1/5 创建服务账号与目录 $DIR"

id -u instorematchd >/dev/null 2>&1 || useradd --system --no-create-home \
    --shell /usr/sbin/nologin instorematchd
mkdir -p "$DIR"

echo "==> 2/5 复制程序文件"
install -m 644 "$SRC/instorematchd.py" "$SRC/test_protocol.py" "$SRC/README.md" "$DIR/"
chown -R instorematchd:instorematchd "$DIR"

echo "==> 3/5 生成 systemd 单元（--host-override $HOST_OVERRIDE）"
sed -e "s|__HOST_OVERRIDE__|$HOST_OVERRIDE|g" \
    -e "s|__LOBBY_PORT__|$LOBBY_PORT|g" \
    -e "s|__RELAY_PORT__|$RELAY_PORT|g" \
    "$SRC/instorematchd.service.template" > /etc/systemd/system/instorematchd.service

# 管理员视图的 token（可选）：通过环境变量传，不写进仓库/单元文件
#   sudo IMD_ADMIN_TOKEN=你的token bash instorematchd/install.sh <域名>
if [ -n "${IMD_ADMIN_TOKEN:-}" ]; then
    printf 'IMD_ADMIN_TOKEN=%s\n' "$IMD_ADMIN_TOKEN" > /etc/instorematchd.env
    chmod 600 /etc/instorematchd.env
    echo "    已写入管理员 token 到 /etc/instorematchd.env（权限 600）"
elif [ -f /etc/instorematchd.env ]; then
    echo "    没给 IMD_ADMIN_TOKEN：沿用 /etc/instorematchd.env 里原来那个 token"
else
    echo "    没给 IMD_ADMIN_TOKEN：公开看板照常（keychip/IP 已打码），/admin 不开放"
fi

echo "==> 4/5 启动服务"
systemctl daemon-reload
# 用 restart 而不是 `enable --now`：已经跑着的话也要重启，才能用上新拷贝的代码。
# （如果当前跑的是有"吞 SIGTERM"bug 的旧代码，这里会等满 TimeoutStopSec 才被杀掉，
#  看起来像卡住 —— 属正常，TimeoutStopSec 就是为这种情况准备的兜底。）
systemctl enable instorematchd
systemctl restart instorematchd

# 起不来就别继续跑自检了 —— 那样测的是"别人的服务"，会假装通过
sleep 2
if ! systemctl is-active --quiet instorematchd; then
    echo
    echo "!! instorematchd 没起来，最近日志：" >&2
    journalctl -u instorematchd -n 15 --no-pager 2>/dev/null || true
    echo >&2
    echo "   常见原因是 $LOBBY_PORT/$RELAY_PORT 被别的进程占着，看看是谁：" >&2
    ss -tlnp 2>/dev/null | grep -E ":($LOBBY_PORT|$RELAY_PORT)\b" || echo "   （端口没被占，那就是别的错误，看上面日志）" >&2
    echo >&2
    echo "   如果是别的服务占着：sudo systemctl disable --now <那个服务名>" >&2
    exit 1
fi
systemctl --no-pager --lines=0 status instorematchd || true

echo "==> 5/5 自检"
# 自检要连我们刚装的这套端口（默认 20100/20101，自定义端口时靠环境变量传进去）；
# token 一起透传，给了的话自检会连管理员视图也验一遍。
NYD_LOBBY="$LOBBY_PORT" NYD_RELAY="$RELAY_PORT" IMD_ADMIN_TOKEN="${IMD_ADMIN_TOKEN:-}" \
    "$PY" "$DIR/test_protocol.py" \
    || echo "（协议自测有失败项，看上面输出）"
echo
echo "看板： http://$HOST_OVERRIDE:$LOBBY_PORT/"
echo "在线： curl -s http://127.0.0.1:$LOBBY_PORT/online"
echo
echo "别忘了放行 TCP $LOBBY_PORT 和 $RELAY_PORT（ufw / firewalld / 云安全组）。"
echo "客户端的 WorldLink.toml 写： LobbyUrl=\"http://$HOST_OVERRIDE:$LOBBY_PORT\""
