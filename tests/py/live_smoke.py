#!/usr/bin/env python3
# 对着**一台真在跑的大厅**跑一轮"各种情况"的烟测。
#
#   python3 tests/py/live_smoke.py                     # 默认打 isakio.cn
#   IMD_LIVE_HOST=1.2.3.4 python3 tests/py/live_smoke.py
#
# 和其它测试的区别：test_e2e / test_edge 都在本地起一个临时服务端，跑的再全也只是
# 自己这台机器；这一份是**打线上**，验的是"部署上去的那份代码到底是不是这个样子"。
# 所以它不放进 run_all.sh（会往公开大厅里临时开房），上线之后单独跑一次。
#
# 每个场景自己收摊：结束前都会关房、断开连接。跑完看一眼末尾的 /online 是不是 0/0。
#
# 覆盖：正常流程、房主先开打、目标不在线、反复重试、挂起超时、房主中途掉线、
#       第二个房客、房间 TTL 与续报。

import json
import os
import subprocess
import sys
import time
import urllib.error
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))          # <仓库>/tests/py
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
sys.path.insert(0, HERE)
from linkproto import (MockClient, Msg, stub_u32,
                       CTL_TCP_CONNECT, CTL_TCP_ACCEPT, CTL_TCP_CLOSE,
                       CTL_HEARTBEAT, DATA_SEND, PROTO_TCP)

SERVER = os.environ.get("IMD_LIVE_HOST", "isakio.cn")
LOBBY = int(os.environ.get("IMD_LIVE_LOBBY", "20100"))
RELAY = int(os.environ.get("IMD_LIVE_RELAY", "20101"))

PASS, FAIL, NOTE = [], [], []


def check(ok, what, extra=""):
    (PASS if ok else FAIL).append(what)
    print("  %s %s%s" % ("✓" if ok else "✗", what, "" if ok else "   " + str(extra)))


def note(text):
    NOTE.append(text)
    print("  · " + text)


def http(method, path, body=None):
    req = urllib.request.Request(
        "http://%s:%d%s" % (SERVER, LOBBY, path), method=method,
        data=json.dumps(body, ensure_ascii=False).encode("utf-8") if body else None,
        headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=10) as r:
            return r.status, r.read().decode("utf-8")
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", "replace")


def recruit_body(keychip, stub, music_id=12054, name="线上测试"):
    return {"Keychip": keychip, "RecruitInfo": {
        "MechaInfo": {
            "IsJoin": True, "IpAddress": stub, "MusicID": music_id,
            "Entrys": [True, False], "UserIDs": [1240811538, 281474976710657],
            "UserNames": [name, "ＧＵＥＳＴ"], "IconIDs": [700101, 1],
            "FumenDifs": [4, -1], "Rateing": [0, 0], "ClassValue": [0, 0],
            "MaxClassValue": [0, 0], "UserType": [3, 0]},
        "MusicID": music_id, "GroupID": 0, "EventModeID": False,
        "JoinNumber": 0, "PartyStance": 0,
        "_startTimeTicks": 639266000000000000, "_recvTimeTicks": 0}}


def my_room_visible(stub):
    _, listing = http("GET", "/recruit/list")
    for line in listing.split("\n"):
        if line.strip() and json.loads(line)["RecruitInfo"]["MechaInfo"]["IpAddress"] == stub:
            return True
    return False


def drain(client, want_cmd, timeout=15.0):
    """等一条指定命令，返回 (消息, 耗时秒)；超时抛异常。"""
    t0 = time.time()
    deadline = t0 + timeout
    while time.time() < deadline:
        msg = client.recv(timeout=max(0.1, deadline - time.time()))
        if msg.cmd == want_cmd:
            return msg, time.time() - t0
    raise TimeoutError("没等到命令 %d" % want_cmd)


def finish(keychip, stub):
    try:
        http("POST", "/recruit/finish", recruit_body(keychip, stub))
    except Exception:
        pass


# ---------------------------------------------------------------- 场景

def l1_happy_path():
    print("\nL1 正常流程（假玩家当房主，裸客户端当房客）")
    hk, gk = "W9LIVE000001", "W9LIVE000002"
    host = subprocess.Popen(
        [sys.executable, "-B", os.path.join(REPO, "tools", "fake_player.py"),
         "--server", SERVER, "--lobby-port", str(LOBBY), "--relay-port", str(RELAY),
         "--keychip", hk, "--name", "线上假房主", "--music-id", "12054",
         "--interval", "10", "--auto-accept", "--no-dump"],
        stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    guest = None
    try:
        ok = False
        for _ in range(20):
            time.sleep(0.5)
            if my_room_visible(stub_u32(hk)):
                ok = True
                break
        check(ok, "假玩家开房后，大厅里能看到这个房间")

        guest = MockClient(gk, SERVER, RELAY)
        guest.recv()                                   # version=1
        sid, gport = 700001, 60001
        guest.send(Msg(CTL_TCP_CONNECT, proto=PROTO_TCP, sid=sid, src=stub_u32(gk),
                       sport=gport, dst=stub_u32(hk), dport=50100))
        got, dt = drain(guest, CTL_TCP_ACCEPT, 8)
        check(got.sid == sid, "建流被假玩家接受（%.2fs）" % dt)

        payload = "aGVsbG8gZnJvbSBsaXZlIHRlc3Q="
        guest.send(Msg(DATA_SEND, proto=PROTO_TCP, sid=sid, src=stub_u32(gk),
                       sport=gport, dst=stub_u32(hk), dport=50100, data=payload))
        got, dt = drain(guest, DATA_SEND, 8)
        check(got.data == payload, "数据能双向传（回包 %.2fs）" % dt)

        guest.send(Msg(CTL_TCP_CLOSE, proto=PROTO_TCP, sid=sid, src=stub_u32(gk),
                       sport=gport, dst=stub_u32(hk), dport=50100))
        time.sleep(0.5)
        guest.send(Msg(CTL_HEARTBEAT))
        got, _ = drain(guest, CTL_HEARTBEAT, 5)
        check(got.cmd == CTL_HEARTBEAT, "关流之后连接仍然正常")
    finally:
        if guest is not None:
            guest.close()
        host.terminate()
        try:
            host.wait(timeout=5)
        except subprocess.TimeoutExpired:
            host.kill()
        finish(hk, stub_u32(hk))
        time.sleep(0.3)
        check(not my_room_visible(stub_u32(hk)), "假玩家退出后房间被清掉")


def l2_host_starts_playing():
    print("\nL2 房主没等人就开打（关房）——房客正挂着等接流")
    hk, gk = "W9LIVE000003", "W9LIVE000004"
    host = MockClient(hk, SERVER, RELAY)
    host.recv()
    guest = MockClient(gk, SERVER, RELAY)
    guest.recv()
    try:
        http("POST", "/recruit/start", recruit_body(hk, stub_u32(hk)))
        sid = 700002
        guest.send(Msg(CTL_TCP_CONNECT, proto=PROTO_TCP, sid=sid, src=stub_u32(gk),
                       sport=60002, dst=stub_u32(hk), dport=50100))
        time.sleep(0.5)
        host.recv(timeout=2)                     # 房主收到请求，但它不接（等于在打歌）
        t0 = time.time()
        http("POST", "/recruit/finish", recruit_body(hk, stub_u32(hk)))
        try:
            got, _ = drain(guest, CTL_TCP_CLOSE, 5)
            dt = time.time() - t0
            check(got.sid == sid, "关房后 %.2fs 内房客收到 CLOSE" % dt)
            check(dt < 2.0, "是立刻取消，不是等 10 秒挂起超时（%.2fs）" % dt)
        except Exception as e:
            check(False, "关房后房客立刻收到 CLOSE", e)
    finally:
        host.close()
        guest.close()
        finish(hk, stub_u32(hk))


def l3_target_offline():
    print("\nL3 目标（房主）根本不在线")
    gk = "W9LIVE000005"
    guest = MockClient(gk, SERVER, RELAY)
    guest.recv()
    try:
        sid = 700003
        guest.send(Msg(CTL_TCP_CONNECT, proto=PROTO_TCP, sid=sid, src=stub_u32(gk),
                       sport=60003, dst=stub_u32("W9LIVE00DEAD"), dport=50100))
        got, dt = drain(guest, CTL_TCP_CLOSE, 5)
        check(got.sid == sid, "立刻收到 CLOSE（%.2fs）" % dt)
        check(dt < 2.0, "没有干等（%.2fs）" % dt)
    except Exception as e:
        check(False, "立刻收到 CLOSE", e)
    finally:
        guest.close()


def l4_repeated_retries():
    print("\nL4 房客反复点加入 12 次（超过服务端挂起上限 10）")
    hk, gk = "W9LIVE000006", "W9LIVE000007"
    host = MockClient(hk, SERVER, RELAY)
    host.recv()
    guest = MockClient(gk, SERVER, RELAY)
    guest.recv()
    try:
        for i in range(12):
            guest.send(Msg(CTL_TCP_CONNECT, proto=PROTO_TCP, sid=710000 + i,
                           src=stub_u32(gk), sport=60100 + i,
                           dst=stub_u32(hk), dport=50100))
            time.sleep(0.05)
        got, _ = drain(guest, CTL_TCP_CLOSE, 5)
        check(got is not None, "超上限的那几条会被立刻拒掉（第 1 条 CLOSE 已到）")
        closes = 1
        deadline = time.time() + 20
        while closes < 12 and time.time() < deadline:
            try:
                m = guest.recv(timeout=max(0.1, deadline - time.time()))
            except Exception:
                break
            if m.cmd == CTL_TCP_CLOSE:
                closes += 1
        check(closes == 12, "12 次建流都拿到了 CLOSE（实际 %d）" % closes)
        guest.send(Msg(CTL_HEARTBEAT))
        got, _ = drain(guest, CTL_HEARTBEAT, 5)
        check(got.cmd == CTL_HEARTBEAT, "人还在线（旧版这时已经被服务端踢掉了）")
    finally:
        host.close()
        guest.close()
        finish(hk, stub_u32(hk))


def l5_pending_timeout():
    print("\nL5 房主在线但一直不接流（线上默认挂起超时 10 秒）")
    hk, gk = "W9LIVE000008", "W9LIVE000009"
    host = MockClient(hk, SERVER, RELAY)
    host.recv()
    guest = MockClient(gk, SERVER, RELAY)
    guest.recv()
    try:
        sid = 720000
        guest.send(Msg(CTL_TCP_CONNECT, proto=PROTO_TCP, sid=sid, src=stub_u32(gk),
                       sport=60009, dst=stub_u32(hk), dport=50100))
        got, dt = drain(guest, CTL_TCP_CLOSE, 20)
        check(got.sid == sid, "%.1fs 后收到 CLOSE" % dt)
        check(8.0 <= dt <= 14.0, "回收时间接近 10 秒（%.1fs）" % dt)
    except Exception as e:
        check(False, "超时回收并回 CLOSE", e)
    finally:
        host.close()
        guest.close()


def l6_host_drops_mid_stream():
    print("\nL6 已经建好流的房主突然掉线，房客继续发包")
    hk, gk = "W9LIVE00000A", "W9LIVE00000B"
    host = MockClient(hk, SERVER, RELAY)
    host.recv()
    guest = MockClient(gk, SERVER, RELAY)
    guest.recv()
    try:
        sid, gport = 730000, 60010
        guest.send(Msg(CTL_TCP_CONNECT, proto=PROTO_TCP, sid=sid, src=stub_u32(gk),
                       sport=gport, dst=stub_u32(hk), dport=50100))
        host.recv(timeout=3)
        host.send(Msg(CTL_TCP_ACCEPT, proto=PROTO_TCP, sid=sid, src=stub_u32(hk),
                      sport=50100, dst=stub_u32(gk), dport=gport))
        drain(guest, CTL_TCP_ACCEPT, 5)
        host.close()                                  # 房主拔网线
        time.sleep(0.5)
        guest.send(Msg(DATA_SEND, proto=PROTO_TCP, sid=sid, src=stub_u32(gk),
                       sport=gport, dst=stub_u32(hk), dport=50100,
                       data="cGluZw=="))
        got, dt = drain(guest, CTL_TCP_CLOSE, 5)
        check(got.sid == sid, "房客发包时收到 CLOSE（%.2fs）" % dt)
    except Exception as e:
        check(False, "房主掉线后房客能收到 CLOSE", e)
    finally:
        host.close()
        guest.close()


def l7_room_ttl_and_refresh():
    print("\nL7 房间 30 秒 TTL vs 客户端每 10 秒续报")
    hk = "W9LIVE00000C"
    stub = stub_u32(hk)
    try:
        http("POST", "/recruit/start", recruit_body(hk, stub))
        check(my_room_visible(stub), "开房后立刻能看到")
        # 模仿真客户端：每 10 秒 POST 一次 /recruit/start（大厅 TTL 是 30 秒）
        t0 = time.time()
        next_refresh = t0 + 10
        while time.time() - t0 < 40:
            time.sleep(1)
            if time.time() >= next_refresh:
                next_refresh += 10
                http("POST", "/recruit/start", recruit_body(hk, stub))
        check(my_room_visible(stub),
              "每 10 秒续报、坚持 40 秒，房间仍然挂在大厅（旧行为 30 秒就没了）")

        note("停止续报，等 TTL 把它收走…")
        time.sleep(32)
        check(not my_room_visible(stub), "停止续报 ~32 秒后房间自动消失（TTL 兜底仍然有效）")
    finally:
        finish(hk, stub)


def l8_second_guest():
    print("\nL8 第二个房客（房间其实只收一个人）")
    hk, g1k, g2k = "W9LIVE00000D", "W9LIVE00000E", "W9LIVE00000F"
    host = MockClient(hk, SERVER, RELAY)
    host.recv()
    g1 = MockClient(g1k, SERVER, RELAY)
    g1.recv()
    g2 = MockClient(g2k, SERVER, RELAY)
    g2.recv()
    try:
        sid1, sid2 = 740000, 740001
        g1.send(Msg(CTL_TCP_CONNECT, proto=PROTO_TCP, sid=sid1, src=stub_u32(g1k),
                    sport=60011, dst=stub_u32(hk), dport=50100))
        host.recv(timeout=3)
        host.send(Msg(CTL_TCP_ACCEPT, proto=PROTO_TCP, sid=sid1, src=stub_u32(hk),
                      sport=50100, dst=stub_u32(g1k), dport=60011))
        drain(g1, CTL_TCP_ACCEPT, 5)
        check(True, "第一个房客正常进房")

        g2.send(Msg(CTL_TCP_CONNECT, proto=PROTO_TCP, sid=sid2, src=stub_u32(g2k),
                    sport=60012, dst=stub_u32(hk), dport=50100))
        host.recv(timeout=3)                          # 房主收得到，但它不会再 Accept
        try:
            got, dt = drain(g2, CTL_TCP_CLOSE, 20)
            check(got.sid == sid2, "第二个房客 %.1fs 后被自动放弃（不会卡住）" % dt)
        except Exception as e:
            check(False, "第二个房客不会永远卡住", e)
    finally:
        host.close()
        g1.close()
        g2.close()
        finish(hk, stub_u32(hk))


def main():
    print("== 线上实测：%s:%d / %d ==" % (SERVER, LOBBY, RELAY))
    t0 = time.time()
    for fn in (l1_happy_path, l2_host_starts_playing, l3_target_offline,
               l4_repeated_retries, l5_pending_timeout, l6_host_drops_mid_stream,
               l8_second_guest, l7_room_ttl_and_refresh):
        try:
            fn()
        except Exception as e:
            check(False, "%s 抛异常" % fn.__name__, e)
    print("\n耗时 %.0f 秒" % (time.time() - t0))
    print("\n--- 收摊后的大厅状态 ---")
    print("  /online =", http("GET", "/online")[1].strip())
    print()
    if FAIL:
        print("失败 %d 项：" % len(FAIL))
        for f in FAIL:
            print("  -", f)
        return 1
    print("全部通过（%d 项）" % len(PASS))
    return 0


if __name__ == "__main__":
    sys.exit(main())
