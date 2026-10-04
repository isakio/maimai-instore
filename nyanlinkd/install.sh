#!/usr/bin/env bash
# nyanlinkd 一键安装（systemd），适用于 Debian / Ubuntu 等常见 Linux。
#
#   sudo bash install.sh <你的域名或公网IP> [大厅端口] [中继端口]
#
# 例：sudo bash install.sh 203.0.113.10
#     sudo bash install.sh maimai.example.com 20100 20101
#
# 装完记得在防火墙 / 云厂商安全组放行这两个 TCP 端口。
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
DIR=/opt/nyanlinkd
PY=/usr/bin/python3

# nyanlinkd 用了 `str | None` 这类 3.10+ 语法
if ! "$PY" -c 'import sys; sys.exit(0 if sys.version_info >= (3, 10) else 1)' 2>/dev/null; then
    echo "需要 Python 3.10 或更高版本（当前: $("$PY" -V 2>&1 || echo 未安装)）" >&2
    exit 1
fi

echo "==> 1/5 创建服务账号与目录 $DIR"
id -u nyanlinkd >/dev/null 2>&1 || useradd --system --no-create-home \
    --shell /usr/sbin/nologin nyanlinkd
mkdir -p "$DIR"

echo "==> 2/5 复制程序文件"
install -m 644 "$SRC/nyanlinkd.py" "$SRC/test_protocol.py" "$SRC/README.md" "$DIR/"
chown -R nyanlinkd:nyanlinkd "$DIR"

echo "==> 3/5 生成 systemd 单元（--host-override $HOST_OVERRIDE）"
sed -e "s|__HOST_OVERRIDE__|$HOST_OVERRIDE|g" \
    -e "s|__LOBBY_PORT__|$LOBBY_PORT|g" \
    -e "s|__RELAY_PORT__|$RELAY_PORT|g" \
    "$SRC/nyanlinkd.service.template" > /etc/systemd/system/nyanlinkd.service

echo "==> 4/5 启动服务"
systemctl daemon-reload
systemctl enable --now nyanlinkd
sleep 1
systemctl --no-pager --lines=0 status nyanlinkd || true

echo "==> 5/5 自检"
"$PY" "$DIR/test_protocol.py" || echo "（协议自测有失败项，看上面输出）"
echo
echo "看板： http://$HOST_OVERRIDE:$LOBBY_PORT/"
echo "在线： curl -s http://127.0.0.1:$LOBBY_PORT/online"
echo
echo "别忘了放行 TCP $LOBBY_PORT 和 $RELAY_PORT（ufw / firewalld / 云安全组）。"
echo "客户端的 WorldLink.toml 写： LobbyUrl=\"http://$HOST_OVERRIDE:$LOBBY_PORT\""
