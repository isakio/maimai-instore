#!/usr/bin/env python3
"""
instorematchd 协议自测：模拟两个客户端，把注册 / 心跳 / 开房 / 建流 / 传数据 / 关流
全部走一遍，顺带检查公开看板确实脱敏了、/admin 没 token 进不去。
改完服务端跑一次，能立刻知道有没有改坏。

  python3 instorematchd.py --bind 127.0.0.1 --lobby-port 21100 --relay-port 21101 &
  python3 test_protocol.py

环境变量：NYD_HOST / NYD_LOBBY / NYD_RELAY 换目标，IMD_ADMIN_TOKEN 顺带测管理员视图。
"""

import hashlib
import json
import os
import socket
import sys
import time
import urllib.error
import urllib.request
from urllib.parse import quote

# 默认连生产端口；本地自测时用环境变量覆盖，例如：
#   NYD_LOBBY=21100 NYD_RELAY=21101 python3 test_protocol.py
HOST = os.environ.get("NYD_HOST", "127.0.0.1")
LOBBY = int(os.environ.get("NYD_LOBBY", 20100))
RELAY = int(os.environ.get("NYD_RELAY", 20101))
# 设了的话顺带检查管理员视图（install.sh 会把 IMD_ADMIN_TOKEN 透传进来）
ADMIN = os.environ.get("IMD_ADMIN_TOKEN", "")
K1, K2 = "W1111111111", "W2222222222"


def stub(keychip: str) -> int:
    d = hashlib.md5(keychip.encode()).digest()
    return (d[0] << 24) | (d[1] << 16) | (d[2] << 8) | d[3]


def conn():
    s = socket.create_connection((HOST, RELAY))
    s.settimeout(3)
    return s


def send(s, line):
    s.sendall((line + "\n").encode())


def recv(s):
    buf = b""
    while not buf.endswith(b"\n"):
        chunk = s.recv(1)
        if not chunk:
            break
        buf += chunk
    return buf.decode().strip()


def http(method, path, body=None):
    req = urllib.request.Request(
        f"http://{HOST}:{LOBBY}{path}", method=method,
        data=json.dumps(body).encode() if body else None,
        headers={"Content-Type": "application/json"})
    return urllib.request.urlopen(req, timeout=3).read().decode()


def http_code(path):
    """只要状态码（403 之类会抛 HTTPError，这里当返回值用）"""
    try:
        urllib.request.urlopen(f"http://{HOST}:{LOBBY}{path}", timeout=3)
        return 200
    except urllib.error.HTTPError as exc:
        return exc.code


FAILED = []


def check(cond, msg):
    print(f"  {'✓' if cond else '✗'} {msg}")
    if not cond:
        FAILED.append(msg)


def main():
    print("1) 注册")
    c1, c2 = conn(), conn()
    send(c1, f"1,1,,,,,,,,,,,,,,,{K1}")
    check("version=1" in recv(c1), "c1 收到 version=1")
    send(c2, f"1,1,,,,,,,,,,,,,,,{K2}")
    check("version=1" in recv(c2), "c2 收到 version=1")

    print("2) 心跳")
    send(c1, "1,3")
    check(recv(c1) == "1,3", "心跳原样回包")

    print("3) 大厅")
    rec = {"Keychip": K1, "RecruitInfo": {
        "MechaInfo": {"IsJoin": True, "IpAddress": stub(K1), "MusicID": 12054,
                      "UserNames": ["测试玩家", "GUEST"], "FumenDifs": [4, -1]},
        "MusicID": 12054, "GroupID": 0, "EventModeID": False}}
    check(json.loads(http("POST", "/recruit/start", rec))["ok"], "开房成功")
    listing = http("GET", "/recruit/list")
    check("测试玩家" in listing and "Keychip" not in listing, "房间列表有数据且不泄露 Keychip")
    online = json.loads(http("GET", "/online"))
    check(online == {"totalUsers": 2, "activeRecruits": 1}, f"/online = {online}")
    check(json.loads(http("GET", "/info"))["relayPort"] == RELAY, "/info 中继端口正确")
    check(json.loads(http("GET", "/api/status"))["online"] == 2, "/api/status 在线数正确")

    print("3.5) 公开看板脱敏 / 管理员视图")
    raw = http("GET", "/api/status")
    check(json.loads(raw)["masked"] is True, "公开 /api/status 是脱敏视图（masked=True）")
    check(K1 not in raw and "测试玩家" in raw, "公开视图里没有完整 keychip（玩家名保留）")
    check(http_code("/admin") == 403, "不带 token 访问 /admin 被拒（403）")
    check(http_code("/admin?token=wrong-token") == 403, "token 不对同样被拒（403）")
    if ADMIN:
        tok = quote(ADMIN, safe="")
        check(http_code(f"/admin?token={tok}") == 200, "/admin 带对 token 正常打开")
        check(json.loads(http("GET", f"/api/status?token={tok}"))["masked"] is False,
              "带 token 的 /api/status 是完整视图（masked=False）")
    else:
        print("  · 没给 IMD_ADMIN_TOKEN，跳过管理员视图检查")

    print("4) 中继：建流 / 接流 / 传数据")
    sid = 424242
    send(c1, f"1,4,6,{sid},{stub(K1)},5000,{stub(K2)},50100")
    check(recv(c2).startswith(f"1,4,6,{sid}"), "c2 收到建流请求")
    send(c2, f"1,5,6,{sid},{stub(K2)},50100,{stub(K1)},5000")
    check(recv(c1).startswith(f"1,5,6,{sid}"), "c1 收到接流确认")
    # 注意：数据字段固定在索引 16，中间 8 个是预留位
    send(c1, f"1,21,6,{sid},{stub(K1)},5000,{stub(K2)},50100," + "," * 8 + "aGVsbG8=")
    got = recv(c2)
    check("aGVsbG8=" in got and str(stub(K1)) in got, f"c2 收到数据: {got}")

    print("5) 关流清理（原版会泄漏）")
    send(c1, f"1,7,6,{sid}")
    time.sleep(0.3)
    streams = [c["streams"] for c in json.loads(http("GET", "/api/status"))["clients"]]
    check(streams == [0, 0], f"双方流表清空: {streams}")

    print("6) 关房")
    check(json.loads(http("POST", "/recruit/finish", rec))["ok"], "关房成功")
    check(json.loads(http("GET", "/online"))["activeRecruits"] == 0, "房间数归零")

    print("7) 容错：乱码消息不应拖垮服务")
    send(c1, "这不是一条合法的消息")
    send(c2, "1,999")
    time.sleep(0.2)
    send(c1, "1,3")
    check(recv(c1) == "1,3", "发送乱码后连接仍然可用")

    print()
    if FAILED:
        print(f"失败 {len(FAILED)} 项：")
        for f in FAILED:
            print("  -", f)
        return 1
    print("全部通过")
    return 0


if __name__ == "__main__":
    sys.exit(main())
