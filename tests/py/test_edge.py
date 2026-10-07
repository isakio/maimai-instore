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
                       CTL_START, CTL_TCP_CONNECT, CTL_TCP_ACCEPT, CTL_TCP_CLOSE, CTL_HEARTBEAT,
                       DATA_BROADCAST, DATA_SEND, PROTO_TCP, PROTO_UDP)

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

    def __init__(self, pending_timeout=10, max_rooms=200, admin_token=None, **extra):
        self.log = tempfile.NamedTemporaryFile("w+", suffix=".log", delete=False)
        self.proc = None
        argv = [sys.executable, "-B", SERVER, "--bind", HOST,
                "--lobby-port", str(LOBBY), "--relay-port", str(RELAY),
                "--recruit-ttl", "30", "--heartbeat-timeout", "30",
                "--pending-timeout", str(pending_timeout),
                "--max-rooms", str(max_rooms), "--log-level", "INFO"]
        if admin_token:
            argv += ["--admin-token", admin_token]
        # 第二轮加的开关：--max-clients / --max-clients-per-ip / --send-timeout /
        # --lobby-timeout / --lobby-max-threads（下划线形式传进来更顺手）。
        for key, val in extra.items():
            argv += ["--" + key.replace("_", "-"), str(val)]
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

    def threads(self):
        """服务端进程当前的线程数（第二轮 S3 用它验"慢连接不涨线程"）。"""
        try:
            with open("/proc/%d/status" % self.proc.pid) as fh:
                for line in fh:
                    if line.startswith("Threads:"):
                        return int(line.split()[1])
        except OSError:
            pass
        return -1


def online_count():
    _, body = http("GET", "/online")
    return json.loads(body)["totalUsers"]


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
    # 服务端关连接时，客户端可能是干净 EOF（recv 回 b""），也可能因为"我们还有没发完的数据"
    # 收到 RST（recv 抛异常）。两种都算"服务端把这条连接断了" —— 只认 b"" 会偶发假红（踩过）。
    closed, got = False, b"?"
    try:
        got = raw.recv(64)
        closed = (got == b"")
    except Exception:
        closed = True
    raw.close()
    check(closed, "不带 keychip 的注册 → 服务端直接关掉这条连接（收到 %r）" % got)

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
    closed, got = False, b"?"
    try:
        got = raw.recv(64)
        closed = (got == b"")
    except Exception:
        closed = True
    raw.close()
    check(closed, "不注册狂发消息 → 限流后断开（收到 %r）" % got)


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


def case_oversize_line():
    print("11) 单行超过 1 MiB → 只跳过这一行，不断开连接")
    # 服务端 StreamReader 的 limit 就是 RELAY_LINE_LIMIT（1 MiB）。发一条**不带换行**、
    # 超过 limit 的数据：asyncio 的 readline() 会走"找不到分隔符且超限"那条路 ——
    # 它把 LimitOverrunError 转成 ValueError 抛出（并已把超长缓冲清掉）。
    # 以前这个 ValueError 没被接住，会掉到最外层 except → break → 整条连接被踢。
    kc = "W9EDGEBIG001"
    srv = Server()
    try:
        c = MockClient(kc, HOST, RELAY)
        c.recv()                               # 注册回包
        try:
            # 分块发，确保服务端在读的时候缓冲确实越过 limit（一次全塞进去也行，
            # 但分块能稳定命中"超限时还没见到换行"这个分支）。
            block = b"x" * 65536
            for _ in range(17):                # 17 * 64 KiB = 1088 KiB > 1 MiB
                c.sock.sendall(block)
                time.sleep(0.01)
            time.sleep(0.3)                    # 给服务端 readline 走到超限分支
            c.sock.sendall(b"\n")              # 这条坏行到此结束
            # 连接必须还活着：退一条心跳，要有心跳回包。
            c.send(Msg(CTL_HEARTBEAT))
            got = c.recv(timeout=3.0)
            check(got.cmd == CTL_HEARTBEAT,
                  "超长行之后连接仍然活着（心跳有回包）", got.readable())
        except Exception as e:
            check(False, "超长行之后连接仍然活着（心跳有回包）", str(e))
        c.close()
        check("Traceback" not in srv.log_text(),
              "超长行跳过时服务端没有 traceback")
    finally:
        srv.stop()


def case_reregister_identity():
    print("12) 同一条连接换 keychip 注册 → 身份/在线数不膨胀（S1）")
    srv = Server()
    try:
        c = MockClient("W9EDGEREG001", HOST, RELAY)
        c.recv()
        for i in range(50):
            c.send(Msg(CTL_START, data="W9EDGEREG%03d" % i))
            time.sleep(0.004)
        time.sleep(0.4)
        n = online_count()
        check(n <= 1, "换 50 次身份后 /online <= 1（实际 %d）" % n)
        c.close()
        time.sleep(0.8)
        check(online_count() == 0, "断开后在线数归零（实际 %d）" % online_count())
        check("Traceback" not in srv.log_text(), "没有 traceback")
    finally:
        srv.stop()


def case_nested_body_types():
    print("13) 请求体嵌套字段类型不符 → 回 4xx，不断连、不 traceback（S2）")
    srv = Server()
    try:
        cases = [
            ("RecruitInfo=字符串", {"Keychip": "W9EDGENEST1", "RecruitInfo": "hello"}),
            ("RecruitInfo=列表", {"Keychip": "W9EDGENEST2", "RecruitInfo": ["x"]}),
            ("MechaInfo=字符串", {"Keychip": "W9EDGENEST3",
                                  "RecruitInfo": {"MechaInfo": "x"}}),
            ("MechaInfo=列表", {"Keychip": "W9EDGENEST4",
                                "RecruitInfo": {"MechaInfo": [1]}}),
            ("Keychip=数字", {"Keychip": 12345,
                              "RecruitInfo": {"MechaInfo": {"IpAddress": 1}}}),
            ("Keychip=对象", {"Keychip": {"a": 1},
                              "RecruitInfo": {"MechaInfo": {"IpAddress": 1}}}),
        ]
        for name, body in cases:
            for path in ("/recruit/start", "/recruit/finish"):
                try:
                    code, _ = http("POST", path, body)
                except Exception as exc:              # 连接被掐断会在这里冒出来
                    code = "断连(%s)" % type(exc).__name__
                check(code in (400, 404),
                      "%s 走 %s 回 4xx 而不是断连（%s）" % (name, path, code))

        # UserNames / FumenDifs 是怪类型时，开房 + /online + 看板都不能 500 / 断连
        kc = "W9EDGENEST5"
        b = recruit_body(kc, stub_u32(kc))
        b["RecruitInfo"]["MechaInfo"]["UserNames"] = 123
        b["RecruitInfo"]["MechaInfo"]["FumenDifs"] = 5
        try:
            code, _ = http("POST", "/recruit/start", b)
        except Exception as exc:
            code = "断连(%s)" % type(exc).__name__
        check(code == 200, "UserNames/FumenDifs 类型怪但能开房（%s）" % code)
        for path in ("/online", "/api/status"):
            try:
                code, _ = http("GET", path)
            except Exception as exc:
                code = "断连(%s)" % type(exc).__name__
            check(code == 200, "怪字段不影响 %s（%s）" % (path, code))
        check("Traceback" not in srv.log_text(), "没有 traceback")
    finally:
        srv.stop()


def case_lobby_threads_bounded():
    print("14) 大厅慢连接不再无限耗线程（S3，--lobby-max-threads 4 / timeout 2s）")
    srv = Server(lobby_max_threads=4, lobby_timeout=2)
    try:
        base = srv.threads()
        socks = []
        for _ in range(20):
            s = socket.create_connection((HOST, LOBBY), timeout=2)
            # 只发半个请求头（声明了 body 也不发），挂着
            s.sendall(b"POST /recruit/start HTTP/1.1\r\nHost: x\r\nContent-Length: 100000\r\n")
            socks.append(s)
        time.sleep(1.0)
        grew = srv.threads() - base
        check(grew <= 6, "20 条半开连接只占 <=6 个线程（上限 4，实际涨 %d）" % grew)
        for s in socks:
            s.close()
        time.sleep(4.0)                       # 等读取超时把残留连接收掉
        check(srv.threads() - base <= 2,
              "关掉后线程回落到基线附近（实际涨 %d）" % (srv.threads() - base))
        code, _ = http("GET", "/online")
        check(code == 200, "大厅仍然可用（HTTP %s）" % code)
        check("Traceback" not in srv.log_text(), "没有 traceback")
    finally:
        srv.stop()


def case_stuck_reader():
    print("15) 只连不读的对端不再堵死发送方（S4，--send-timeout 2）")
    srv = Server(send_timeout=2, heartbeat_timeout=60)
    try:
        kb, ka = "W9EDGESLOW01", "W9EDGESLOW02"
        b = MockClient(kb, HOST, RELAY)
        b.recv()
        b.sock.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 4096)   # 缩小接收缓冲，早点堵
        a = MockClient(ka, HOST, RELAY)
        a.recv()
        # 先建一条真流（S6 修好后，DATA_SEND 必须挂在已建成的流上）
        sid = 60604
        a.send(Msg(CTL_TCP_CONNECT, proto=PROTO_TCP, sid=sid, src=stub_u32(ka), sport=66004,
                   dst=stub_u32(kb), dport=50100))
        b.recv(timeout=2.0)                     # B 收下 CONNECT 后就再也不读了
        b.send(Msg(CTL_TCP_ACCEPT, proto=PROTO_TCP, sid=sid, src=stub_u32(kb), sport=50100,
                   dst=stub_u32(ka), dport=66004))
        a.recv(timeout=2.0)                     # A 收到 ACCEPT，流建成
        a.sock.settimeout(2.0)
        big = "x" * 900000
        for _ in range(40):
            try:
                a.send(Msg(DATA_SEND, proto=PROTO_TCP, sid=sid, src=stub_u32(ka),
                           sport=66004, dst=stub_u32(kb), dport=50100, data=big))
            except Exception:
                break
            time.sleep(0.02)
        time.sleep(2.5)
        check(online_count() <= 1,
              "只连不读的 B 在 send_timeout 后被服务端断开（/online 只剩 A，实际 %d）"
              % online_count())
        # A 不该被永久堵住：给它一点时间把积压清掉，然后必须能拿到心跳回包
        ok = False
        deadline = time.time() + 6
        while time.time() < deadline:
            try:
                a.sock.settimeout(1.0)
                a.send(Msg(CTL_HEARTBEAT))
                m = a.recv(timeout=1.0)
            except Exception:
                time.sleep(0.3)
                continue
            if m.cmd == CTL_HEARTBEAT:
                ok = True
                break
        check(ok, "灌大包后发送方 A 仍能拿到心跳回包（不再被无限期堵住）")
        check("Traceback" not in srv.log_text(), "没有 traceback")
        a.close()
        b.close()
    finally:
        srv.stop()


def case_accept_peer_check():
    print("16) 第三方 ACCEPT 被拒、真房主 ACCEPT 成功（S5）")
    srv = Server()
    try:
        ka, kh, kb = "W9EDGEACC001", "W9EDGEACC002", "W9EDGEACC003"
        a = MockClient(ka, HOST, RELAY); a.recv()
        h = MockClient(kh, HOST, RELAY); h.recv()
        b = MockClient(kb, HOST, RELAY); b.recv()
        sa, sh, sb = stub_u32(ka), stub_u32(kh), stub_u32(kb)
        sid = 60601
        a.send(Msg(CTL_TCP_CONNECT, proto=PROTO_TCP, sid=sid, src=sa, sport=66001,
                   dst=sh, dport=50100))
        h.recv(timeout=2.0)
        # 第三方 B 冒充房主去接（ACCEPT 要发给发起方 A）
        b.send(Msg(CTL_TCP_ACCEPT, proto=PROTO_TCP, sid=sid, src=sb, sport=50111,
                   dst=sa, dport=66001))
        got = b.recv(timeout=2.0)
        check(got.cmd == CTL_TCP_CLOSE, "第三方 B 的 ACCEPT 被回 CLOSE 拒绝（%s）" % got.readable())
        # 对照：真房主 H 用同样的 sid 接，必须成功
        h.send(Msg(CTL_TCP_ACCEPT, proto=PROTO_TCP, sid=sid, src=sh, sport=50100,
                   dst=sa, dport=66001))
        got2 = a.recv(timeout=2.0)
        check(got2.cmd == CTL_TCP_ACCEPT and got2.src == sh,
              "真房主 H 的 ACCEPT 成功（%s）" % got2.readable())
        a.close(); h.close(); b.close()
    finally:
        srv.stop()


def case_host_disconnect_notifies():
    print("17) 房主掉线 → 挂起方立刻收到 CLOSE（S6）")
    srv = Server(pending_timeout=30)
    try:
        hk, gk = "W9EDGEDROP01", "W9EDGEDROP02"
        h = MockClient(hk, HOST, RELAY); h.recv()
        g = MockClient(gk, HOST, RELAY); g.recv()
        sid = 60602
        g.send(Msg(CTL_TCP_CONNECT, proto=PROTO_TCP, sid=sid, src=stub_u32(gk), sport=66002,
                   dst=stub_u32(hk), dport=50100))
        h.recv(timeout=2.0)                    # 房主确实收到了建流请求
        h.close()                              # 房主硬退
        t0 = time.time()
        got = g.recv(timeout=3.0)
        dt = time.time() - t0
        check(got.cmd == CTL_TCP_CLOSE and got.sid == sid,
              "房主掉线 %.2fs 内挂起方收到 CLOSE（%s）" % (dt, got.readable()))
        g.close()
    finally:
        srv.stop()


def case_unsolicited_send_dropped():
    print("18) 不属于任何已建流的 TCP DATA_SEND 被丢弃（S6）")
    srv = Server()
    try:
        ka, kv = "W9EDGEINJ001", "W9EDGEINJ002"
        a = MockClient(ka, HOST, RELAY); a.recv()
        v = MockClient(kv, HOST, RELAY); v.recv()
        a.send(Msg(DATA_SEND, proto=PROTO_TCP, sid=12345, src=stub_u32(ka), sport=1,
                   dst=stub_u32(kv), dport=50100, data="aGVsbG8="))
        leaked = False
        try:
            v.recv(timeout=1.5)
            leaked = True
        except Exception:
            leaked = False
        check(not leaked, "未建流的 DATA_SEND 不再被转发给受害者")
        # 对照：正常建流后仍然能发数据
        sid = 60603
        a.send(Msg(CTL_TCP_CONNECT, proto=PROTO_TCP, sid=sid, src=stub_u32(ka), sport=66003,
                   dst=stub_u32(kv), dport=50100))
        v.recv(timeout=2.0)
        v.send(Msg(CTL_TCP_ACCEPT, proto=PROTO_TCP, sid=sid, src=stub_u32(kv), sport=50100,
                   dst=stub_u32(ka), dport=66003))
        a.recv(timeout=2.0)
        a.send(Msg(DATA_SEND, proto=PROTO_TCP, sid=sid, src=stub_u32(ka), sport=66003,
                   dst=stub_u32(kv), dport=50100, data="aGk="))
        got = v.recv(timeout=2.0)
        check(got.cmd == DATA_SEND and got.data == "aGk=",
              "对照组：建流后数据正常转发（%s）" % got.readable())
        a.close(); v.close()
    finally:
        srv.stop()


def case_relay_client_cap():
    print("19) 中继连接上限：全局 / 每 IP（S7）")
    srv = Server(max_clients=4)
    try:
        conns = []
        for i in range(4):
            c = MockClient("W9EDGECAP%03d" % i, HOST, RELAY); c.recv(); conns.append(c)
        extra = MockClient("W9EDGECAP999", HOST, RELAY)
        rejected = False
        try:
            extra.recv(timeout=1.5)
        except Exception:
            rejected = True
        check(rejected, "超过 --max-clients 的第 5 条注册被拒绝（连接被关）")
        check(online_count() == 4, "在线数停在上限 4（实际 %d）" % online_count())
        for c in conns:
            c.close()
        extra.close()
    finally:
        srv.stop()

    srv = Server(max_clients_per_ip=3)
    try:
        conns = []
        for i in range(3):
            c = MockClient("W9EDGEIPC%03d" % i, HOST, RELAY); c.recv(); conns.append(c)
        extra = MockClient("W9EDGEIPC999", HOST, RELAY)
        rejected = False
        try:
            extra.recv(timeout=1.5)
        except Exception:
            rejected = True
        check(rejected, "超过 --max-clients-per-ip 的第 4 条被拒绝")
        check(online_count() == 3, "在线数停在上限 3（实际 %d）" % online_count())
        for c in conns:
            c.close()
        extra.close()
    finally:
        srv.stop()


def case_broadcast_rate_limited():
    print("20) UDP 广播被限速（S7）")
    srv = Server()
    try:
        cs = []
        for i in range(8):
            c = MockClient("W9EDGEBC%03d" % i, HOST, RELAY); c.recv(); cs.append(c)
        sender = cs[0]
        for _ in range(50):
            sender.send(Msg(DATA_BROADCAST, proto=PROTO_UDP, sid=1,
                            src=stub_u32(sender.keychip), sport=5000, dst=0, dport=5001,
                            data="aGk="))
        time.sleep(0.8)
        got = 0
        for c in cs[1:]:
            c.sock.settimeout(0.05)
            while True:
                try:
                    chunk = c.sock.recv(65536)
                except Exception:
                    break
                if not chunk:
                    break
                got += chunk.count(b"\n")
        # 不限速会是 7 个收件人 × 50 条 = 350；限速后每个收件人 <= BROADCAST_RATE_MAX(10)
        check(got <= 7 * 10, "50 条广播被限速到 <=70 次转发（实际 %d）" % got)
        check(got >= 1, "限速没有把广播全挡死（实际 %d）" % got)
        for c in cs:
            c.close()
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
    case_oversize_line()
    # 第二轮边界/压力审计的回归（S1–S7）
    case_reregister_identity()
    case_nested_body_types()
    case_lobby_threads_bounded()
    case_stuck_reader()
    case_accept_peer_check()
    case_host_disconnect_notifies()
    case_unsolicited_send_dropped()
    case_relay_client_cap()
    case_broadcast_rate_limited()

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
