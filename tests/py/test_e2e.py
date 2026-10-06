#!/usr/bin/env python3
"""端到端测试：起一个真的 instorematchd，用"行为像 mod"的客户端跑一局。

测的是协议链路（不是游戏画面）：
  两个客户端注册 → 房主开房（HTTP）→ 房客拉列表 → 房客建流 → 房主接流
  → 房客发数据 → 房主收到同样的字节 → 关流 → 关房

服务器默认用本仓库里的那套（相对本文件定位，跟仓库放哪儿无关）：
  <仓库>/instorematchd/instorematchd.py
（可以用 IMD_SERVER_PY 换路径，用 IMD_LOBBY/IMD_RELAY 换端口）
"""

import base64
import json
import os
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from linkproto import (MockClient, Msg, stub_u32, u32_to_ip,
                       CTL_HEARTBEAT, CTL_TCP_CONNECT, CTL_TCP_ACCEPT,
                       CTL_TCP_CLOSE, DATA_SEND, PROTO_TCP)

# 默认按本文件的位置找仓库里的服务端：tests/py/ -> tests/ -> <仓库>/
SERVER = os.environ.get("IMD_SERVER_PY", os.path.normpath(os.path.join(
    os.path.dirname(os.path.abspath(__file__)), "..", "..", "instorematchd", "instorematchd.py")))
HOST = "127.0.0.1"
LOBBY = int(os.environ.get("IMD_LOBBY", "21200"))
RELAY = int(os.environ.get("IMD_RELAY", "21201"))

PASS = []
FAIL = []


def check(ok, what, extra=""):
    (PASS if ok else FAIL).append(what)
    print("  %s %s%s" % ("✓" if ok else "✗", what, "" if ok else "  " + extra))


def http(method, path, body=None, expect=200):
    req = urllib.request.Request(
        "http://%s:%d%s" % (HOST, LOBBY, path), method=method,
        data=json.dumps(body, ensure_ascii=False).encode("utf-8") if body else None,
        headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=5) as resp:
            return resp.status, resp.read().decode("utf-8")
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", "replace")


def wait_port(port, timeout=10.0):
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            with socket.create_connection((HOST, port), timeout=0.5):
                return True
        except OSError:
            time.sleep(0.15)
    return False


def recruit_body(keychip, stub, music_id=12054, name="测试房主"):
    """字段抄自真实招募报文（maimai/logs 里抓的那份）。"""
    return {"Keychip": keychip, "RecruitInfo": {
        "MechaInfo": {
            "IsJoin": True, "IpAddress": stub, "MusicID": music_id,
            "Entrys": [True, False], "UserIDs": [1240811538, 281474976710657],
            "UserNames": [name, "ＧＵＥＳＴ"], "IconIDs": [700101, 1],
            "FumenDifs": [4, -1], "Rateing": [0, 0], "ClassValue": [0, 0],
            "MaxClassValue": [0, 0], "UserType": [3, 0]},
        "MusicID": music_id, "GroupID": 0, "EventModeID": False, "JoinNumber": 0,
        "PartyStance": 0, "_startTimeTicks": 639266000000000000, "_recvTimeTicks": 0}}


def main():
    if not os.path.exists(SERVER):
        print("找不到服务端脚本：%s（用 IMD_SERVER_PY 指定）" % SERVER)
        return 1

    print("0) 起一个临时服务器 %s:%d / %d" % (HOST, LOBBY, RELAY))
    log = open("/tmp/instorelink-e2e-server.log", "wb")
    proc = subprocess.Popen(
        [sys.executable, SERVER, "--bind", HOST, "--lobby-port", str(LOBBY),
         "--relay-port", str(RELAY), "--host-override", "127.0.0.1"],
        stdout=log, stderr=subprocess.STDOUT)
    try:
        if not wait_port(LOBBY) or not wait_port(RELAY):
            print("服务器没起来，看 /tmp/instorelink-e2e-server.log")
            return 1
        run_checks()
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            proc.kill()
        log.close()

    print()
    if FAIL:
        print("失败 %d 项：" % len(FAIL))
        for f in FAIL:
            print("  -", f)
        return 1
    print("全部通过（%d 项）" % len(PASS))
    return 0


def run_checks():
    kc_host, kc_guest = "W9666666666", "W9777777777"
    host_stub, guest_stub = stub_u32(kc_host), stub_u32(kc_guest)

    print("1) 两个客户端注册到中继")
    a = MockClient(kc_host, HOST, RELAY)
    b = MockClient(kc_guest, HOST, RELAY)
    check("version=" in (a.recv().data or ""), "房主收到 version=1")
    check("version=" in (b.recv().data or ""), "房客收到 version=1")

    print("2) 心跳")
    a.send(Msg(CTL_HEARTBEAT))
    check(a.recv().cmd == CTL_HEARTBEAT, "心跳原样回包")

    print("3) 房主开房（HTTP），房客拉列表")
    status, body = http("POST", "/recruit/start", recruit_body(kc_host, host_stub))
    check(status == 200, "POST /recruit/start 成功", body)
    status, listing = http("GET", "/recruit/list")
    room = None
    for line in listing.split("\n"):
        if line.strip():
            room = json.loads(line)
    check(room is not None and room["RecruitInfo"]["MechaInfo"]["IpAddress"] == host_stub,
          "列表里能按伪 IP 找到房间")
    check("Keychip" not in listing, "列表里不带 keychip")

    status, body = http("GET", "/online")
    check(json.loads(body)["activeRecruits"] == 1, "/online 房间数 = 1")
    status, body = http("GET", "/info")
    check(json.loads(body)["relayPort"] == RELAY, "/info 返回正确的中继端口")

    print("4) 建流 / 接流")
    sid, bind_port = 123456789, 60001
    b.send(Msg(CTL_TCP_CONNECT, proto=PROTO_TCP, sid=sid, src=guest_stub,
               sport=bind_port, dst=host_stub, dport=bind_port))
    got = a.recv()
    check(got.cmd == CTL_TCP_CONNECT and got.sid == sid and got.src == guest_stub,
          "房主收到建流请求（src 被服务端改写成房客伪 IP）", got.readable())
    check(got.dport == bind_port, "建流请求落在房主监听的端口上")

    a.send(Msg(CTL_TCP_ACCEPT, proto=PROTO_TCP, sid=sid, src=host_stub,
               sport=bind_port, dst=guest_stub, dport=bind_port))
    got = b.recv()
    check(got.cmd == CTL_TCP_ACCEPT and got.sid == sid, "房客收到接流确认", got.readable())

    print("5) 传数据")
    payload = "aGVsbG8gaW5zdG9yZWxpbms="          # "hello instorelink"
    b.send(Msg(DATA_SEND, proto=PROTO_TCP, sid=sid, src=guest_stub, sport=bind_port,
               dst=host_stub, dport=bind_port, data=payload))
    got = a.recv()
    check(got.cmd == DATA_SEND and got.data == payload, "房主收到原样的数据", got.readable())
    check(got.src == guest_stub and got.dst == host_stub,
          "报文的 src/dst 被服务端改写成双方伪 IP")

    print("5b) 大包（base64 之后超过 asyncio 默认的 64 KiB 行上限）也要能转发")
    # 踩过：服务端没给 start_server 传 limit=，一行超过 64 KiB 就 LimitOverrunError →
    # 整条连接被静默断开（游戏那边表现是"打着打着两边都掉了"）。这里用 70 KB 卡住它。
    big = base64.b64encode(("x" * 70000).encode()).decode()
    b.send(Msg(DATA_SEND, proto=PROTO_TCP, sid=sid, src=guest_stub, sport=bind_port,
               dst=host_stub, dport=bind_port, data=big))
    got = a.recv(timeout=15)
    check(got.cmd == DATA_SEND and got.data == big,
          "70 KB 的包能原样转发（长度 %d）" % len(big), got.readable()[:120])

    print("6) 关流（我们补上了 sid，服务端会清流表）")
    b.send(Msg(CTL_TCP_CLOSE, proto=PROTO_TCP, sid=sid, src=guest_stub,
               sport=bind_port, dst=host_stub, dport=bind_port))
    time.sleep(0.3)
    status, body = http("GET", "/api/status")
    streams = [c["streams"] for c in json.loads(body)["clients"]]
    check(streams == [0, 0], "两边流表都清空了：%s" % streams)

    print("7) 关房")
    status, body = http("POST", "/recruit/finish", recruit_body(kc_host, host_stub))
    check(status == 200, "POST /recruit/finish 成功", body)
    status, body = http("GET", "/online")
    check(json.loads(body)["activeRecruits"] == 0, "房间数归零")

    print("8) 目标不在线：回 CLOSE（而不是让人干等），且不应打崩服务端")
    b.send("这不是一条合法消息")
    b.send(Msg(DATA_SEND, proto=PROTO_TCP, sid=1, src=guest_stub, sport=1,
               dst=stub_u32("W0000000000"), dport=1, data=payload))
    got = b.recv()
    check(got.cmd == CTL_TCP_CLOSE and got.sid == 1,
          "目标不在线时服务端主动回 CTL_TCP_CLOSE", got.readable())
    b.send(Msg(CTL_HEARTBEAT))
    check(b.recv().cmd == CTL_HEARTBEAT, "发完垃圾消息后连接仍然可用")

    a.close()
    b.close()


if __name__ == "__main__":
    sys.exit(main())
