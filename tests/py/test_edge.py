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
                       CTL_TCP_CONNECT, CTL_TCP_ACCEPT, CTL_TCP_CLOSE, CTL_HEARTBEAT, PROTO_TCP)

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

    def __init__(self, pending_timeout=10, max_rooms=200, admin_token=None):
        self.log = tempfile.NamedTemporaryFile("w+", suffix=".log", delete=False)
        self.proc = None
        argv = [sys.executable, "-B", SERVER, "--bind", HOST,
                "--lobby-port", str(LOBBY), "--relay-port", str(RELAY),
                "--recruit-ttl", "30", "--heartbeat-timeout", "30",
                "--pending-timeout", str(pending_timeout),
                "--max-rooms", str(max_rooms), "--log-level", "INFO"]
        if admin_token:
            argv += ["--admin-token", admin_token]
        self.proc = subprocess.Popen(
            argv,
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

    # 中继上"不带 keychip 的注册"：以前 register() 抛 ValueError，被上层 LOG.exception
    # 打成一条完整 traceback + ERROR（公开端口随手可触发），而且这条连接还留着 ——
    # 之后每条消息再刷一句"未注册就发消息"的警告。现在应该是"警告 + 关连接"。
    raw = socket.create_connection((HOST, RELAY), timeout=5)
    raw.settimeout(5)
    raw.sendall(b"1,1\n")                      # 只有 cmd=1，没有 keychip
    time.sleep(0.3)
    got = b"?"
    try:
        got = raw.recv(64)
    except Exception:
        pass
    raw.close()
    check(got == b"", "不带 keychip 的注册 → 服务端直接关掉这条连接（收到 %r）" % got)

    # 一直不注册、只是狂发消息：以前每条都打一句 warning（公开端口能把日志刷爆），
    # 现在限流成最多 3 条 + 断开这条连接。
    raw = socket.create_connection((HOST, RELAY), timeout=5)
    raw.settimeout(5)
    try:
        for _ in range(50):
            raw.sendall(b"1,3\n")
    except Exception:
        pass
    time.sleep(0.5)
    got = b"?"
    try:
        got = raw.recv(64)
    except Exception:
        pass
    raw.close()
    check(got == b"", "不注册狂发消息 → 限流后断开（收到 %r）" % got)


def case_malformed_body():
    print("5) 畸形请求体：非 JSON 对象不能把连接掐断")
    kc = "W9EDGEBODY01"
    stub = stub_u32(kc)
    code, _ = http("POST", "/recruit/start", recruit_body(kc, stub))
    check(code == 200, "对照组：正常 body 能开房（HTTP %s）" % code)

    # 数组 / 字符串 / 数字都能被 json.loads 解出来，但 handler 里是 `(data or {}).get(...)`，
    # 非 dict 会 AttributeError —— 在 HTTP 线程里抛出去就是"连接被掐断"（客户端看到
    # RemoteDisconnected），而不是 400。未鉴权接口，一个 [1,2,3] 就能触发。
    for name, body in (("JSON 数组", [1, 2, 3]), ("JSON 字符串", "hello"),
                       ("JSON 数字", 42), ("JSON 数组(关房)", ["x"])):
        path = "/recruit/finish" if name.endswith("(关房)") else "/recruit/start"
        try:
            code, _ = http("POST", path, body)
        except Exception as exc:              # 连接被掐断会在这里冒出来
            code = "连接被掐断(%s)" % type(exc).__name__
        check(code in (400, 404), "%s 回 4xx 而不是断连（%s %s）" % (name, path, code))

    code, _ = http("POST", "/recruit/finish", recruit_body(kc, stub))
    check(code == 200, "畸形请求之后服务端照常工作（HTTP %s）" % code)

    # IpAddress 不是整数时，以前 int(stub) 会抛 ValueError/TypeError —— 同一个坑：
    # 处理函数里抛出去 = 这条连接被掐断 + 日志里一条 traceback。
    for name, ip in (("字符串", "abc"), ("列表", [1]), ("字典", {"a": 1}),
                     ("null", None), ("true", True)):
        b = recruit_body("W9EDGEBODY02", 1)
        b["RecruitInfo"]["MechaInfo"]["IpAddress"] = ip
        for path in ("/recruit/start", "/recruit/finish"):
            try:
                code, _ = http("POST", path, b)
            except Exception as exc:
                code = "断连(%s)" % type(exc).__name__
            check(code in (400, 404),
                  "IpAddress=%s 走 %s 回 4xx 而不是断连（%s）" % (name, path, code))


def case_pending_timeout():
    print("6) 一直没人接流 → 到点回收（--pending-timeout 3）")
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
    print("7) 房主不在中继上的房间不公开（幽灵房）")
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
    print("8) 开房限速 / 房间总数上限")
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


def case_stream_cap():
    print("10) 已建成的流也有上限（防自连刷爆服务端流表）")
    srv = Server(pending_timeout=10)
    try:
        kc = "W9EDGECAP001"
        st = stub_u32(kc)
        c = MockClient(kc, HOST, RELAY)
        c.recv()
        try:
            for i in range(120):
                c.send(Msg(CTL_TCP_CONNECT, proto=PROTO_TCP, sid=7000 + i,
                           src=st, sport=64000 + i, dst=st, dport=50100))
                c.send(Msg(CTL_TCP_ACCEPT, proto=PROTO_TCP, sid=7000 + i,
                           src=st, sport=50100, dst=st, dport=64000 + i))
            time.sleep(0.8)
            n = sum(cl["streams"] for cl in
                    json.loads(urllib.request.urlopen(
                        "http://%s:%d/api/status" % (HOST, LOBBY), timeout=5).read()
                    )["clients"])
            check(n <= 64, "自连 120 次后服务端流数被压在上限内（实际 %d）" % n)
            check(n >= 1, "上限没有把正常建流也一起堵死（实际 %d）" % n)
        finally:
            c.close()
        check("Traceback" not in srv.log_text(), "刷流表过程中没有 traceback")
    finally:
        srv.stop()


def case_admin_token():
    print("9) 管理员 token：非 ASCII 的 token 不能让请求断连")
    srv = Server(admin_token="edge-secret")
    try:
        # hmac.compare_digest 对非 ASCII 的 **str** 会抛 TypeError —— 以前 token 直接拿去比，
        # 一个 emoji 就让请求断连（客户端看到 RemoteDisconnected）+ 日志里一条 traceback。
        for label, tok in (("中文", "%E6%97%A5%E6%9C%AC%E8%AA%9E"),
                           ("emoji", "%F0%9F%98%80")):
            try:
                code, _ = http("GET", "/api/status?token=" + tok)
            except Exception as exc:
                code = "断连(%s)" % type(exc).__name__
            check(code == 200, "%s token 走脱敏视图、不断连（HTTP %s）" % (label, code))
            try:
                code, _ = http("GET", "/admin?token=" + tok)
            except Exception as exc:
                code = "断连(%s)" % type(exc).__name__
            check(code == 403, "%s token 进 /admin 回 403（HTTP %s）" % (label, code))
        code, _ = http("GET", "/admin?token=edge-secret")
        check(code == 200, "正确 token 仍然能进 /admin（HTTP %s）" % code)
        check("Traceback" not in srv.log_text(), "带 token 的服务端日志里也没有 traceback")
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
        case_malformed_body()
        case_pending_timeout()
        case_ghost_room_not_published()
    finally:
        srv.stop()

    # 客户端能发出来的畸形消息，不该在服务端打出一堆未捕获异常的 traceback
    check("Traceback" not in srv.log_text(), "服务端日志里没有未捕获异常的 traceback")
    check(srv.log_text().count("未注册就发消息") <= 3,
          "不注册刷消息不会把日志刷爆（最多 3 条告警，实际 %d）"
          % srv.log_text().count("未注册就发消息"))

    print("  服务端日志摘录：")
    for line in srv.log_text().splitlines():
        if any(k in line for k in ("回收", "取消挂起", "拒绝新流", "目标不在线", "开房被")):
            print("    " + line)

    case_limits()
    case_admin_token()
    case_stream_cap()

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
