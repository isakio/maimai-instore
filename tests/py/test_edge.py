#!/usr/bin/env python3
# 异常流程测试：把"没人按正常剧本走"的那些情况跑一遍。
#
# 正常流程在 test_e2e.py 里（注册 → 开房 → 建流 → 传数据 → 关流 → 关房）。
# 这一份专门盯那些**正常流程碰不到、但玩家天天会碰到**的情况：
#   1. 房主没等人就开打（关房）→ 还挂着的"加入"要被立刻取消，不能干等
#   2. 目标（房主）压根不在线 → 房客要立刻知道失败，而不是卡在连接中
#   3. 房客反复点"加入" → 不能被服务端踢下线，连接必须还在
#   4. 一直没人接流 → 到点回收
#   5. Keychip 与 IpAddress 对不上 / 缺 Keychip / 冒名关别人的房 → 拒掉
#   6. 开房限速与房间总数上限
#
# 为什么单列一份：这几条以前都是"只写一行日志就完事"，玩家侧表现为
# "点了加入一直转圈"或者"莫名其妙掉线"，光看服务端日志根本对不上号。
#
# 本文件自己起临时服务端，端口 21300 / 21301（IMD_EDGE_LOBBY / IMD_EDGE_RELAY 可覆盖）。

import json
import os
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from linkproto import (MockClient, Msg, stub_u32,
                       CTL_TCP_CONNECT, CTL_TCP_CLOSE, CTL_HEARTBEAT, PROTO_TCP)

SERVER = os.environ.get("IMD_SERVER_PY", os.path.normpath(os.path.join(
    os.path.dirname(os.path.abspath(__file__)), "..", "..", "instorematchd", "instorematchd.py")))
HOST = "127.0.0.1"
LOBBY = int(os.environ.get("IMD_EDGE_LOBBY", "21300"))
RELAY = int(os.environ.get("IMD_EDGE_RELAY", "21301"))

PASS = []
FAIL = []


def check(ok, what, extra=""):
    (PASS if ok else FAIL).append(what)
    print("  %s %s%s" % ("✓" if ok else "✗", what, "" if ok else "  " + extra))


def http(method, path, body=None):
    req = urllib.request.Request(
        "http://%s:%d%s" % (HOST, LOBBY, path), method=method,
        data=json.dumps(body, ensure_ascii=False).encode("utf-8") if body else None,
        headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=5) as r:
            return r.status, r.read().decode("utf-8")
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", "replace")


def wait_port(port, timeout=10.0):
    end = time.time() + timeout
    while time.time() < end:
        try:
            with socket.create_connection((HOST, port), timeout=0.5):
                return True
        except OSError:
            time.sleep(0.15)
    return False


def recruit_body(keychip, stub, music_id=12054, name="测试房主"):
    """字段抄自真实招募报文（和 test_e2e.py 里那份一致）。"""
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


class Server:
    """起一个临时 instorematchd，并确认**这个进程**真的起来了。"""

    def __init__(self, pending_timeout=10, max_rooms=200):
        self.log = tempfile.NamedTemporaryFile("w+", suffix=".log", delete=False)
        self.proc = subprocess.Popen(
            [sys.executable, "-B", SERVER, "--bind", HOST,
             "--lobby-port", str(LOBBY), "--relay-port", str(RELAY),
             "--recruit-ttl", "30", "--heartbeat-timeout", "30",
             "--pending-timeout", str(pending_timeout),
             "--max-rooms", str(max_rooms), "--log-level", "INFO"],
            stdout=self.log, stderr=subprocess.STDOUT)
        # 不能只看 wait_port：上一轮的实例还占着端口时它会假报成功，
        # 于是测试其实连到了旧服务端上（踩过，会得出完全相反的结论）。
        end = time.time() + 10
        while time.time() < end:
            if self.proc.poll() is not None:
                raise RuntimeError("服务端退出：" + self.log_text())
            if "中继已启动" in self.log_text() and wait_port(LOBBY):
                return
            time.sleep(0.2)
        raise RuntimeError("服务端没起来：" + self.log_text())

    def stop(self):
        self.proc.terminate()
        try:
            self.proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            self.proc.kill()
        self.log.flush()

    def log_text(self):
        with open(self.log.name, "r", errors="replace") as f:
            return f.read()


def case_offline_target():
    print("1) 目标不在线 → 房客立刻收到 CLOSE")
    guest = MockClient("W9EDGE000001", HOST, RELAY)
    guest.recv()
    sid = 3001
    guest.send(Msg(CTL_TCP_CONNECT, proto=PROTO_TCP, sid=sid,
                   src=stub_u32("W9EDGE000001"), sport=60001,
                   dst=stub_u32("W9EDGE0000FF"), dport=50100))
    try:
        got = guest.recv(timeout=2.0)
        check(got.cmd == CTL_TCP_CLOSE and got.sid == sid, "立刻收到 CLOSE", got.readable())
        check(got.dport == 60001, "CLOSE 的 dport 指向房客自己的端口（%s）" % got.dport)
        check("目标不在线" in (got.data or ""),
              "CLOSE 里带着拒绝原因，排查时不用猜（%s）" % got.data)
    except Exception as e:
        check(False, "立刻收到 CLOSE", str(e))
    guest.close()


def case_host_starts_without_waiting():
    print("2) 房主没等人就开打 → 挂起的建流被取消（不用等超时）")
    hk, gk = "W9EDGE000002", "W9EDGE000003"
    host = MockClient(hk, HOST, RELAY)
    host.recv()
    guest = MockClient(gk, HOST, RELAY)
    guest.recv()
    http("POST", "/recruit/start", recruit_body(hk, stub_u32(hk)))
    sid = 3002
    guest.send(Msg(CTL_TCP_CONNECT, proto=PROTO_TCP, sid=sid,
                   src=stub_u32(gk), sport=60002, dst=stub_u32(hk), dport=50100))
    time.sleep(0.4)
    host.recv(timeout=1.0)                  # 房主收到请求（但它在打歌，不会 Accept）
    started = time.time()
    http("POST", "/recruit/finish", recruit_body(hk, stub_u32(hk)))
    try:
        got = guest.recv(timeout=2.0)
        dt = time.time() - started
        check(got.cmd == CTL_TCP_CLOSE and got.sid == sid,
              "关房后 %.2fs 内收到 CLOSE" % dt, got.readable())
    except Exception as e:
        check(False, "关房后立刻收到 CLOSE", str(e))
    host.close()
    guest.close()


def case_repeated_retries():
    print("3) 反复点加入 → 不被踢下线，且每条都有 CLOSE")
    hk, gk = "W9EDGE000004", "W9EDGE000005"
    host = MockClient(hk, HOST, RELAY)
    host.recv()
    guest = MockClient(gk, HOST, RELAY)
    guest.recv()
    total = 12                                # 超过服务端的挂起上限（10）
    for i in range(total):
        guest.send(Msg(CTL_TCP_CONNECT, proto=PROTO_TCP, sid=4000 + i,
                       src=stub_u32(gk), sport=61000 + i, dst=stub_u32(hk), dport=50100))
        time.sleep(0.05)
    got = []
    deadline = time.time() + 10
    while len(got) < total and time.time() < deadline:
        try:
            got.append(guest.recv(timeout=max(0.1, deadline - time.time())))
        except Exception:
            break
    closes = [m for m in got if m.cmd == CTL_TCP_CLOSE]
    check(len(closes) == total, "%d 次建流都拿到了 CLOSE（实际 %d）" % (total, len(closes)))
    reasons = [m.data or "" for m in closes]
    check(any("挂起已满" in r for r in reasons),
          "超过上限那几条会说明原因（挂起已满）：%s" % sorted(set(reasons)))
    guest.send(Msg(CTL_HEARTBEAT))
    try:
        check(guest.recv(timeout=2.0).cmd == CTL_HEARTBEAT, "连接仍然活着（心跳有回包）")
    except Exception as e:
        check(False, "连接仍然活着（心跳有回包）", str(e))
    host.close()
    guest.close()


def case_bad_keychip():
    print("4) 身份校验：Keychip / IpAddress / 冒名关房")
    code, body = http("POST", "/recruit/start", recruit_body("W9EDGE000006", 12345))
    check(code == 400, "伪造 IpAddress 被拒（%s）" % code, body)
    code, body = http("POST", "/recruit/start",
                      {"RecruitInfo": recruit_body("W9EDGE000006", 12345)["RecruitInfo"]})
    check(code == 400, "缺 Keychip 被拒（%s）" % code, body)
    hk = "W9EDGE000007"
    code, _ = http("POST", "/recruit/start", recruit_body(hk, stub_u32(hk)))
    check(code == 200, "正常开房仍然成功（%s）" % code)
    code, _ = http("POST", "/recruit/finish", recruit_body("W9EDGE0000AA", stub_u32(hk)))
    check(code == 403, "别人不能用你的 IpAddress 关你的房（%s）" % code)
    code, _ = http("POST", "/recruit/finish", recruit_body(hk, stub_u32(hk)))
    check(code == 200, "本人关房成功（%s）" % code)


def case_pending_timeout():
    print("5) 一直没人接流 → 到点回收（--pending-timeout 3）")
    hk, gk = "W9EDGE000008", "W9EDGE000009"
    host = MockClient(hk, HOST, RELAY)
    host.recv()
    guest = MockClient(gk, HOST, RELAY)
    guest.recv()
    sid = 5001
    guest.send(Msg(CTL_TCP_CONNECT, proto=PROTO_TCP, sid=sid,
                   src=stub_u32(gk), sport=62001, dst=stub_u32(hk), dport=50100))
    started = time.time()
    try:
        got = guest.recv(timeout=8.0)
        dt = time.time() - started
        check(got.cmd == CTL_TCP_CLOSE and got.sid == sid, "%.1fs 后收到 CLOSE" % dt, got.readable())
        check(2.0 <= dt <= 7.0, "回收时间落在 3s 附近（实际 %.1fs）" % dt)
    except Exception as e:
        check(False, "超时后收到 CLOSE", str(e))
    host.close()
    guest.close()


def visible_rooms():
    _, listing = http("GET", "/recruit/list")
    out = []
    for line in listing.split("\n"):
        if line.strip():
            out.append(json.loads(line)["RecruitInfo"])
    return out


def room_visible(stub):
    for r in visible_rooms():
        if r["MechaInfo"]["IpAddress"] == stub:
            return True
    return False


def case_ghost_room_not_published():
    print("6) 房主不在中继上的房间不公开（幽灵房）")
    kc = "W9EDGEGHOST1"
    stub = stub_u32(kc)
    code, _ = http("POST", "/recruit/start", recruit_body(kc, stub))
    check(code == 200, "上报接口本身仍然接受（HTTP %s）" % code)
    time.sleep(0.3)
    check(not room_visible(stub), "但它不会出现在房间列表里（玩家点都点不到）")

    host = MockClient(kc, HOST, RELAY)          # 房主真正连上中继
    host.recv()
    try:
        http("POST", "/recruit/start", recruit_body(kc, stub))
        check(room_visible(stub), "房主上线之后再报，就正常公开了")
    finally:
        host.close()
        http("POST", "/recruit/finish", recruit_body(kc, stub))


def case_limits():
    print("7) 开房限速 / 房间总数上限")
    srv = Server(pending_timeout=10, max_rooms=2)
    try:
        codes = []
        for i in range(4):
            k = "W9EDGELIM%02d" % i
            codes.append(http("POST", "/recruit/start", recruit_body(k, stub_u32(k)))[0])
        check(codes == [200, 200, 429, 429], "房间总数上限生效：%s" % codes)
    finally:
        srv.stop()

    srv = Server()
    try:
        codes = []
        for i in range(25):
            k = "W9EDGERATE%02d" % i
            codes.append(http("POST", "/recruit/start", recruit_body(k, stub_u32(k)))[0])
        check(codes.count(200) == 20 and codes.count(429) == 5,
              "10 秒内同一来源最多开 20 次（200×%d，429×%d）"
              % (codes.count(200), codes.count(429)))
    finally:
        srv.stop()


def main():
    if not os.path.exists(SERVER):
        print("找不到服务端脚本：%s（用 IMD_SERVER_PY 指定）" % SERVER)
        return 1

    # 主实例统一用 3 秒挂起超时：第 3、5 条都指望它，跑得快一点
    srv = Server(pending_timeout=3)
    try:
        case_offline_target()
        case_host_starts_without_waiting()
        case_repeated_retries()
        case_bad_keychip()
        case_pending_timeout()
        case_ghost_room_not_published()
    finally:
        srv.stop()

    print("  服务端日志摘录：")
    for line in srv.log_text().splitlines():
        if any(k in line for k in ("回收", "取消挂起", "拒绝新流", "目标不在线", "开房被")):
            print("    " + line)

    case_limits()

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
