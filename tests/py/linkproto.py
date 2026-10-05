#!/usr/bin/env python3
"""中继协议的 Python 模型（和 tools/instorelink/LinkProtocol.cs 一一对应）。

用途：
  * 在不用 Windows / 游戏的情况下验证协议规则（tests/test_vectors.py）
  * 模拟一个"行为像 mod"的客户端做端到端测试（tests/test_e2e.py）

规则（和上游 WorldLink 完全一致）：
  一条消息 = 一行 UTF-8，逗号分隔的 17 个字段：
    0=固定 1  1=cmd  2=proto  3=sid  4=src  5=sport  6=dst  7=dport  8..15=预留  16+=data
  序列化时尾部空字段裁掉；解析时 data = 第 16 个字段起重新拼起来。
  伪 IP = md5(keychip) 前 4 字节（大端）当 uint32。
"""

import hashlib
import socket
import struct
import time

PROTO_TCP = 6
PROTO_UDP = 17
PROTO_VERSION = 1
DATA_INDEX = 16

CTL_START = 1
CTL_BIND = 2
CTL_HEARTBEAT = 3
CTL_TCP_CONNECT = 4
CTL_TCP_ACCEPT = 5
CTL_TCP_ACCEPT_ACK = 6
CTL_TCP_CLOSE = 7
DATA_SEND = 21
DATA_BROADCAST = 22

CMD_NAMES = {
    CTL_START: "CTL_START", CTL_BIND: "CTL_BIND", CTL_HEARTBEAT: "CTL_HEARTBEAT",
    CTL_TCP_CONNECT: "CTL_TCP_CONNECT", CTL_TCP_ACCEPT: "CTL_TCP_ACCEPT",
    CTL_TCP_ACCEPT_ACK: "CTL_TCP_ACCEPT_ACK", CTL_TCP_CLOSE: "CTL_TCP_CLOSE",
    DATA_SEND: "DATA_SEND", DATA_BROADCAST: "DATA_BROADCAST",
}

PROTO_NAMES = {PROTO_TCP: "Tcp", PROTO_UDP: "Udp"}


def stub_u32(keychip: str) -> int:
    d = hashlib.md5(keychip.encode("utf-8")).digest()
    return (d[0] << 24) | (d[1] << 16) | (d[2] << 8) | d[3]


def u32_to_ip(value: int) -> str:
    return "%d.%d.%d.%d" % (
        (value >> 24) & 255, (value >> 16) & 255, (value >> 8) & 255, value & 255)


def ip_to_u32(text: str) -> int:
    return struct.unpack(">I", socket.inet_aton(text))[0]


class Msg:
    """一条中继消息。字段名沿用上游（src/dst 是伪 IP 的 uint32）。"""

    __slots__ = ("cmd", "proto", "sid", "src", "sport", "dst", "dport", "data")

    def __init__(self, cmd, proto=None, sid=None, src=None, sport=None,
                 dst=None, dport=None, data=None):
        self.cmd = cmd
        self.proto = proto
        self.sid = sid
        self.src = src
        self.sport = sport
        self.dst = dst
        self.dport = dport
        self.data = data

    def __str__(self):
        fields = [
            "1", str(self.cmd),
            "" if self.proto is None else str(self.proto),
            "" if self.sid is None else str(self.sid),
            "" if self.src is None else str(self.src),
            "" if self.sport is None else str(self.sport),
            "" if self.dst is None else str(self.dst),
            "" if self.dport is None else str(self.dport),
        ]
        fields += [""] * (DATA_INDEX - len(fields))
        fields.append("" if self.data is None else self.data)
        return ",".join(fields).rstrip(",")

    @staticmethod
    def parse(line: str) -> "Msg":
        f = line.split(",")

        def num(i, cast=int):
            if i >= len(f) or f[i] == "":
                return None
            try:
                return cast(f[i])
            except ValueError:
                return None

        return Msg(
            cmd=num(1),
            proto=num(2),
            sid=num(3),
            src=num(4),
            sport=num(5),
            dst=num(6),
            dport=num(7),
            data=",".join(f[DATA_INDEX:]) if len(f) > DATA_INDEX else "",
        )

    def readable(self) -> str:
        """和上游 ToReadableString / 我们 LinkMsg.Readable 一样的日志格式。"""
        import base64
        parts = [CMD_NAMES.get(self.cmd, "CMD_%s" % self.cmd)]
        if self.proto is not None:
            parts.append(PROTO_NAMES.get(self.proto, str(self.proto)))
        if self.sid is not None:
            parts.append("Stream: %d" % self.sid)
        if self.src is not None:
            parts.append("Src: %s:%s" % (u32_to_ip(self.src), self.sport or 0))
        if self.dst is not None:
            parts.append("Dst: %s:%s" % (u32_to_ip(self.dst), self.dport or 0))
        if self.data:
            try:
                parts.append(base64.b64decode(self.data + "=" * (-len(self.data) % 4))
                             .decode("utf-8"))
            except Exception:
                parts.append(self.data)
        return " | ".join(parts)


class MockClient:
    """一个"行为像 mod"的客户端：注册 → 心跳 → 收发数据，队列 key 也照上游那套。"""

    def __init__(self, keychip: str, host: str, port: int):
        self.keychip = keychip
        self.stub = stub_u32(keychip)
        self.sock = socket.create_connection((host, port), timeout=5)
        self.sock.settimeout(5)
        self.buf = b""
        self.send(Msg(CTL_START, data=keychip))

    def send(self, msg: Msg):
        self.sock.sendall((str(msg) + "\n").encode("utf-8"))

    def recv(self, timeout=5.0) -> Msg:
        deadline = time.time() + timeout
        while b"\n" not in self.buf:
            self.sock.settimeout(max(0.1, deadline - time.time()))
            chunk = self.sock.recv(4096)
            if not chunk:
                raise EOFError("连接被关闭")
            self.buf += chunk
        line, self.buf = self.buf.split(b"\n", 1)
        return Msg.parse(line.decode("utf-8", "replace").strip())

    def close(self):
        try:
            self.sock.close()
        except Exception:
            pass
