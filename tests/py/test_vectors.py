#!/usr/bin/env python3
"""协议规则的 Python 侧校验（和 tests/ProtocolTests.cs 用同一批向量）。

为什么两遍都写：C# 那份验证"我们真正发出去的东西"，Python 这份跑起来不需要
Windows / 游戏，方便随时回归；两边向量一致，改协议时哪边漏改都会红。

另外还做一件 C# 那边做不到的事：拿**真实抓包日志**（maimai/logs/*.log）验一遍，
确认我们的模型能解释日志里的每一行。
"""

import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from linkproto import (Msg, stub_u32, u32_to_ip, ip_to_u32, CTL_START, CTL_HEARTBEAT,
                       CTL_TCP_CONNECT, CTL_TCP_ACCEPT, CTL_TCP_CLOSE, DATA_SEND, PROTO_TCP)

LOGS = os.environ.get(
    "MAIMAI_LOGS",
    os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "..", "logs"))

PASS = []
FAIL = []


def check(ok, what, extra=""):
    (PASS if ok else FAIL).append(what)
    print("  %s %s%s" % ("✓" if ok else "✗", what, "" if ok else "  " + extra))


# ---------------------------------------------------------------- 1. 序列化

print("1) 序列化")
check(str(Msg(CTL_HEARTBEAT)) == "1,3", "心跳序列化成 1,3")

connect = Msg(CTL_TCP_CONNECT, proto=PROTO_TCP, sid=424242, src=4078886013,
              sport=5000, dst=3870867194, dport=50100)
check(str(connect) == "1,4,6,424242,4078886013,5000,3870867194,50100",
      "建流消息：没数据时尾部逗号被裁掉")

data = Msg(DATA_SEND, proto=PROTO_TCP, sid=424242, src=4078886013, sport=5000,
           dst=3870867194, dport=50100, data="aGVsbG8=")
check(str(data) == "1,21,6,424242,4078886013,5000,3870867194,50100,,,,,,,,,aGVsbG8=",
      "数据消息：8 个预留空字段 + 下标 16 放数据")

# ---------------------------------------------------------------- 2. 往返

print("2) 解析 → 序列化 往返")
for line in [
    "1,3",
    "1,1,,,,,,,,,,,,,,,W9367886794",
    "1,4,6,424242,4078886013,5000,3870867194,50100",
    "1,5,6,424242,3870867194,50100,4078886013,5000",
    "1,21,6,424242,4078886013,5000,3870867194,50100,,,,,,,,,aGVsbG8=",
    "1,7,6,424242,4078886013,5000,3870867194,50100",
]:
    check(str(Msg.parse(line)) == line, "往返 " + (line[:42] + "…" if len(line) > 45 else line))

parsed = Msg.parse("1,21,6,424242,4078886013,5000,3870867194,50100,,,,,,,,,aGVsbG8=")
check(parsed.src == 4078886013, "src 解析成 uint32（不能被截断）")
check(parsed.data == "aGVsbG8=", "data 解析（下标 16）")
check(parsed.readable() ==
      "DATA_SEND | Tcp | Stream: 424242 | Src: 243.30.220.125:5000 | "
      "Dst: 230.184.190.250:50100 | hello",
      "Readable() 和上游日志格式一致")

# ---------------------------------------------------------------- 3. 伪 IP

print("3) 伪 IP")
check(stub_u32("W9367886794") == 4078886013, "W9367886794 → 4078886013")
check(u32_to_ip(stub_u32("W9367886794")) == "243.30.220.125", "W9367886794 → 243.30.220.125")
check(stub_u32("W9751871074") == 3870867194, "W9751871074 → 3870867194")
check(u32_to_ip(stub_u32("W9751871074")) == "230.184.190.250", "W9751871074 → 230.184.190.250")
check(ip_to_u32(u32_to_ip(4078886013)) == 4078886013, "uint → IP → uint 还原")

# ---------------------------------------------------------------- 4. 真实日志

print("4) 真实抓包日志（%s）" % os.path.normpath(LOGS))
log_files = []
if os.path.isdir(LOGS):
    log_files = [os.path.join(LOGS, f) for f in sorted(os.listdir(LOGS)) if f.endswith(".log")]
if not log_files:
    print("  · 没找到日志目录，跳过（设 MAIMAI_LOGS 指向 maimai/logs 就能跑这段）")
else:
    # 日志里的可读形式： "243.30.220.125 <<< DATA_SEND | Tcp | Stream: 421409813 | Src: ...:57497 | ..."
    line_re = re.compile(
        r"(?P<dir><<<|>>>)\s+(?P<cmd>[A-Z_]+)"
        r"(?:\s+\|\s+(?P<rest>.*))?$")
    field_re = re.compile(r"(Stream|Src|Dst):\s*([^\s|:]+)(?::(\d+))?")
    seen = set()
    parsed_ok = 0
    parsed_bad = 0
    for path in log_files:
        with open(path, encoding="utf-8", errors="replace") as fh:
            for raw in fh:
                m = line_re.search(raw.strip())
                if not m:
                    continue
                cmd, rest = m.group("cmd"), m.group("rest") or ""
                fields = {}
                for fm in field_re.finditer(rest):
                    fields[fm.group(1)] = (fm.group(2), fm.group(3))
                if "Src" not in fields:
                    continue
                key = (cmd, fields.get("Stream", (None,))[0], fields["Src"])
                if key in seen:
                    continue
                seen.add(key)
                # 反向拼一条报文，再用我们的模型解析，看能不能还原出同样的字段
                src_u32 = ip_to_u32(fields["Src"][0])
                msg = Msg.parse("1,%d,6,%s,%d,%s,%d,%s" % (
                    {"CTL_TCP_CONNECT": CTL_TCP_CONNECT, "CTL_TCP_ACCEPT": CTL_TCP_ACCEPT,
                     "DATA_SEND": DATA_SEND, "CTL_TCP_CLOSE": CTL_TCP_CLOSE}.get(cmd, DATA_SEND),
                    fields.get("Stream", (0,))[0], src_u32, fields["Src"][1] or 0,
                    ip_to_u32(fields["Dst"][0]) if "Dst" in fields else 0,
                    fields["Dst"][1] if "Dst" in fields else 0))
                got = msg.readable()
                if cmd in got and fields["Src"][0] in got:
                    parsed_ok += 1
                else:
                    parsed_bad += 1
                    print("    ! %s -> %s" % (cmd, got))
    check(parsed_ok > 0 and parsed_bad == 0,
          "日志里 %d 种报文全部能被模型还原（%d 种）" % (parsed_ok, parsed_ok + parsed_bad))

print()
if FAIL:
    print("失败 %d 项：" % len(FAIL))
    for f in FAIL:
        print("  -", f)
    sys.exit(1)
print("全部通过（%d 项）" % len(PASS))
