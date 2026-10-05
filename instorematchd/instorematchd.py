#!/usr/bin/env python3
"""
instorematchd —— 兼容 WorldLink / NyanLink 客户端 mod 的自建联机服务端

参照 MuNET-OSS/NyanLink（worldlinkd 的 Kotlin 实现）重写，协议完全兼容：
客户端 mod 不需要任何改动，只要 LobbyUrl 指向本服务即可。

只用 Python 标准库，零依赖。

  HTTP 大厅 :20100   /recruit/start  /recruit/finish  /recruit/list  /info  /online
                     /            （网页看板）
                     /api/status  （看板用的 JSON）
  TCP  中继 :20101   行协议，一条消息一行，字段用逗号分隔

协议字段（0 起）：
  0=固定 1   1=命令   2=协议(6=TCP/17=UDP)   3=流ID   4=源IP   5=源端口
  6=目标IP   7=目标端口   8..15=预留   16+=数据(base64，或纯文本)
命令：1=注册 3=心跳 4=建流 5=接流 7=关流 21=发数据 22=广播

相比原版修掉的问题：
  * 处理 CTL_TCP_CLOSE（原版直接忽略，流表只增不减）
  * 房间 TTL、心跳超时可配置
  * 客户端异常/半包不会拖垮整个服务
  * 网页看板：实时看在线玩家和房间，不用再翻日志

针对「异常流程」补的一轮（原版和第一版都没兜）：
  * CTL_TCP_CONNECT 挂起超时：对端一直不 Accept 就回收，并回一条 CTL_TCP_CLOSE
    给请求方，别让它干等（客户端那边没有超时，只能靠服务端说一声）
  * 挂起流超上限只拒这一条，不再把整个连接踢掉
  * 目标不在线 / 接流失败时，主动回 CTL_TCP_CLOSE 给发起方
  * CTL_TCP_CLOSE 转发给对端（原来只清服务端自己的流表）
  * 关房时把该房主名下所有挂起建流一并取消
  * /recruit/start 校验 Keychip 与 IpAddress 一致、限速、限制房间总数
"""

from __future__ import annotations

import argparse
import asyncio
import base64
import hashlib
import hmac
import json
import logging
import os
import re
import signal
import sys
import threading
import time
from collections import deque
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

LOG = logging.getLogger("instorematchd")

# /admin 和完整信息用的 token（由 --admin-token 或环境变量 IMD_ADMIN_TOKEN 传入；
# 不要写进本仓库 —— 仓库是公开的）
ADMIN_TOKEN = ""

# ------------------------------------------------------------ 公开看板脱敏
# 公开的看板 / /api/status 里，keychip 和 IP 一律打码（名字保留）；
# 看全部信息要带 token 访问 /admin?token=…
_KEYCHIP_RE = re.compile(r"\b[AW][0-9A-Za-z][0-9A-Za-z-]{5,}\b")
_IP_RE = re.compile(r"\b(?:\d{1,3}\.){3}\d{1,3}\b")


def mask_keychip(keychip):
    """W9999888877 -> W9999***877"""
    if not keychip:
        return keychip
    s = str(keychip)
    if len(s) <= 8:
        return s[:2] + "***"
    return s[:5] + "*" * (len(s) - 8) + s[-3:]


def mask_ip(ip):
    """203.0.113.10 -> 203.0.x.x"""
    if not ip:
        return ip
    parts = str(ip).split(".")
    if len(parts) == 4:
        return f"{parts[0]}.{parts[1]}.x.x"
    return str(ip)[:4] + "***"


def mask_text(text):
    """把自由文本（事件流水）里出现的 keychip / IP 打码"""
    if not text:
        return text

    def _kc(m):
        v = m.group(0)
        if sum(ch.isdigit() for ch in v) < 4:
            return v                      # 数字太少，当普通单词，不动
        return mask_keychip(v)

    return _IP_RE.sub(lambda m: mask_ip(m.group(0)), _KEYCHIP_RE.sub(_kc, text))

# ------------------------------------------------------------------ 协议常量
CMD_START = 1
CMD_HEARTBEAT = 3
CMD_TCP_CONNECT = 4
CMD_TCP_ACCEPT = 5
CMD_TCP_CLOSE = 7
CMD_SEND = 21
CMD_BROADCAST = 22

PROTO_TCP = 6
PROTO_UDP = 17
PROTO_VERSION = 1
MAX_STREAMS = 10            # 单个客户端同时挂起的建流上限
PENDING_TIMEOUT = 10        # 建流请求多久没人接就回收（秒，可用 --pending-timeout 改）
MAX_ROOMS = 200             # 大厅同时存在的房间上限（可用 --max-rooms 改）
ROOM_RATE_WINDOW = 10.0     # 开房限速窗口（秒）
# 每个来源 IP 在窗口内最多开几次房。放宽到 20 是有意的：实测本体只在"开始招募"
# 那一下发 StartRecruit（5 分钟 6 次），但万一某个版本每帧/每秒重播，20/10s 也不会
# 误伤；同时它仍然是"公开接口不能被刷"的底线。
ROOM_RATE_MAX = 20

CMD_NAMES = {
    CMD_START: "CTL_START", CMD_HEARTBEAT: "CTL_HEARTBEAT",
    CMD_TCP_CONNECT: "CTL_TCP_CONNECT", CMD_TCP_ACCEPT: "CTL_TCP_ACCEPT",
    CMD_TCP_CLOSE: "CTL_TCP_CLOSE", CMD_SEND: "DATA_SEND",
    CMD_BROADCAST: "DATA_BROADCAST",
}


def keychip_to_stub(keychip: str) -> int:
    """和客户端 mod 完全一致的伪 IP 算法：keychip 的 md5 前 4 字节当大端 uint32"""
    d = hashlib.md5(keychip.encode("utf-8")).digest()
    return (d[0] << 24) | (d[1] << 16) | (d[2] << 8) | d[3]


def stub_to_ip(stub: int) -> str:
    return f"{stub >> 24}.{(stub >> 16) & 255}.{(stub >> 8) & 255}.{stub & 255}"


def parse_msg(line: str) -> dict:
    """把一行协议文本解析成字典，字段缺失一律当 None"""
    f = line.split(",")

    def num(i, cast=int):
        if i < len(f) and f[i] != "":
            try:
                return cast(f[i])
            except ValueError:
                return None
        return None

    return {
        "cmd": num(1),
        "proto": num(2),
        "sid": num(3),
        "src": num(4),
        "sport": num(5),
        "dst": num(6),
        "dport": num(7),
        "data": ",".join(f[16:]) if len(f) > 16 else None,
    }


def build_msg(cmd, proto=None, sid=None, src=None, sport=None, dst=None, dport=None,
              data=None) -> str:
    """和客户端 Msg.ToString() 一致的序列化：17 个字段，尾部空字段裁掉"""
    arr = [1, cmd, proto, sid, src, sport, dst, dport,
           None, None, None, None, None, None, None, None, data]
    return ",".join("" if x is None else str(x) for x in arr).rstrip(",")


def ctl(cmd, data=None) -> str:
    return build_msg(cmd, data=data)


def close_msg(proto, sid, src_stub, src_port, dst_stub, dst_port, reason=None) -> str:
    """
    发给某一方的「这条流没了」。

    字段要站在**收件人**的角度填：dst 是收件人、dport 是收件人自己的端口 ——
    客户端是按 `sid + 自己的端口` 找回那条流 / 那次建流的（和它内部队列的 key 一致）。

    reason 会放进 data 字段：客户端会把它打进日志。以前只说"这条流没了"，
    排查时根本分不出是"目标不在线"还是"挂起超了上限"（真踩过）。
    """
    return build_msg(CMD_TCP_CLOSE, proto=proto, sid=sid,
                     src=src_stub, sport=src_port, dst=dst_stub, dport=dst_port,
                     data=reason)


# ------------------------------------------------------------------ 共享状态
class State:
    """中继和大厅共享：在线客户端、房间列表、事件记录"""

    def __init__(self, recruit_ttl: int):
        self.lock = threading.Lock()
        self.clients: dict[int, "RelayClient"] = {}      # stub -> client
        self.recruits: dict[int, dict] = {}              # stub -> {rec, keychip, ts}
        self.room_posts: dict[str, deque] = {}           # 来源IP -> 开房时间戳（限速用）
        self.events: deque = deque(maxlen=200)
        self.started_at = time.time()
        self.recruit_ttl = recruit_ttl
        self.stats = {"registered": 0, "sends": 0, "broadcasts": 0, "connects": 0}

    def log_event(self, kind: str, text: str):
        entry = {"ts": time.time(), "kind": kind, "text": text}
        with self.lock:
            self.events.appendleft(entry)
        LOG.info("[%s] %s", kind, text)

    def prune_recruits(self):
        now = time.time()
        with self.lock:
            gone = [k for k, v in self.recruits.items()
                    if now - v["ts"] > self.recruit_ttl]
            for k in gone:
                self.recruits.pop(k, None)
            # 开房限速表也顺手清一下：只留这个时间窗口里还有记录的来源，
            # 不然一个公开接口攒久了就是一张无限增长的 IP 表。
            stale = [s for s, q in self.room_posts.items()
                     if not q or now - q[-1] > ROOM_RATE_WINDOW]
            for s in stale:
                self.room_posts.pop(s, None)

    def snapshot(self, mask: bool = False) -> dict:
        self.prune_recruits()
        with self.lock:
            clients = [
                {
                    "keychip": c.keychip,
                    "stub": stub_to_ip(c.stub) if c.stub else None,
                    "peer": c.peer_ip,
                    "connected": round(time.time() - c.connected_at),
                    "idle": round(time.time() - c.last_heartbeat, 1),
                    "streams": len(c.streams),
                }
                for c in self.clients.values()
            ]
            recruits = [
                {
                    "host": (r["rec"].get("RecruitInfo", {}).get("MechaInfo", {}) or {}).get("UserNames", [None])[0],
                    "music_id": r["rec"].get("RecruitInfo", {}).get("MusicID")
                                or r["rec"].get("RecruitInfo", {}).get("MechaInfo", {}).get("MusicID"),
                    "difficulty": ((r["rec"].get("RecruitInfo", {}).get("MechaInfo", {}) or {}).get("FumenDifs") or [None])[0],
                    "age": round(time.time() - r["ts"], 1),
                    "stub": stub_to_ip(k),
                }
                for k, r in self.recruits.items()
            ]
            events = list(self.events)[:40]
            stats = dict(self.stats)

        # 公开视图：keychip / IP 打码（名字保留）；/admin?token=… 看原始值
        if mask:
            for c in clients:
                c["keychip"] = mask_keychip(c["keychip"])
                c["stub"] = mask_ip(c["stub"])
                c["peer"] = mask_ip(c["peer"])
            for r in recruits:
                r["stub"] = mask_ip(r["stub"])
            events = [{"ts": e["ts"], "kind": e["kind"], "text": mask_text(e["text"])}
                      for e in events]

        return {
            "uptime": round(time.time() - self.started_at),
            "online": len(clients),
            "rooms": len(recruits),
            "clients": clients,
            "recruits": recruits,
            "events": events,
            "stats": stats,
            "version": PROTO_VERSION,
            "recruit_ttl": self.recruit_ttl,
            "pending_timeout": PENDING_TIMEOUT,
            "masked": mask,
        }


STATE: State | None = None


# ------------------------------------------------------------------ TCP 中继
class RelayClient:
    def __init__(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter,
                 peer_ip: str):
        self.reader = reader
        self.writer = writer
        self.peer_ip = peer_ip
        self.keychip: str | None = None
        self.stub: int | None = None
        self.streams: dict[int, int] = {}      # 流ID -> 对端 stub
        # 流ID -> {"peer": 目标 stub, "proto", "sport", "dport", "ts"}
        # 用 dict 而不是 set：要能按时间回收（以前只增不减，涨到 10 条就把人踢下线）
        self.pending: dict[int, dict] = {}
        self.last_heartbeat = time.time()
        self.connected_at = time.time()
        self._lock = asyncio.Lock()
        self.closed = False

    async def send(self, line: str):
        if self.closed:
            return
        async with self._lock:
            try:
                self.writer.write((line + "\n").encode("utf-8"))
                await self.writer.drain()
            except Exception as exc:
                LOG.debug("发送失败 %s: %s", self.peer_ip, exc)
                await self.close()

    async def close(self):
        if self.closed:
            return
        self.closed = True
        try:
            self.writer.close()
        except Exception:
            pass

    async def register(self, keychip: str):
        if not keychip:
            raise ValueError("注册消息没有 keychip")
        stub = keychip_to_stub(keychip)
        if STATE is None:
            raise RuntimeError("服务未初始化")
        with STATE.lock:
            old = STATE.clients.get(stub)
            if old is not None and old is not self:
                LOG.info("同一身份重新登录，踢掉旧连接: %s", keychip)
                asyncio.create_task(old.close())
            self.keychip = keychip
            self.stub = stub
            STATE.clients[stub] = self
            STATE.stats["registered"] += 1
        STATE.log_event("注册", f"{keychip} 来自 {self.peer_ip} → 伪IP {stub_to_ip(stub)}")
        await self.send(ctl(CMD_START, data=f"version={PROTO_VERSION}"))

    def find_target(self, msg: dict) -> "RelayClient | None":
        stub = None
        if msg["sid"] is not None:
            stub = self.streams.get(msg["sid"])
        if stub is None:
            stub = msg["dst"]
        if stub is None or STATE is None:
            return None
        with STATE.lock:
            return STATE.clients.get(stub)

    async def handle(self, msg: dict):
        cmd = msg["cmd"]
        if cmd == CMD_HEARTBEAT:
            self.last_heartbeat = time.time()
            await self.send(ctl(CMD_HEARTBEAT))
            return

        if cmd == CMD_BROADCAST:
            if msg["proto"] != PROTO_UDP:
                LOG.debug("收到非 UDP 的广播，忽略")
                return
            with STATE.lock:
                targets = list(STATE.clients.values())
                STATE.stats["broadcasts"] += 1
            for c in targets:
                await c.send(build_msg(CMD_BROADCAST, proto=msg["proto"],
                                       sid=msg["sid"], src=self.stub, sport=msg["sport"],
                                       dst=msg["dst"], dport=msg["dport"], data=msg["data"]))
            return

        target = self.find_target(msg)
        if target is None:
            LOG.warning("命令 %s 的目标不在线（dst=%s sid=%s）",
                        CMD_NAMES.get(cmd, cmd),
                        stub_to_ip(msg["dst"]) if msg["dst"] else "-", msg["sid"])
            # 目标不在线时，让发起方**立刻**知道自己白等了。
            # 以前这里只写一条 warning 就完了：房客那边收不到任何回应，而客户端
            # 发起建流之后是没有超时的 —— 表现就是"点了加入，然后永远卡在连接中"。
            if (cmd in (CMD_TCP_CONNECT, CMD_TCP_ACCEPT, CMD_SEND)
                    and msg["sid"] is not None and msg["dst"] is not None):
                await self.send(close_msg(msg["proto"], msg["sid"],
                                          msg["dst"], msg["dport"],
                                          self.stub, msg["sport"],
                                          reason="目标不在线"))
            return

        if cmd == CMD_SEND:
            with STATE.lock:
                STATE.stats["sends"] += 1
            await target.send(build_msg(CMD_SEND, proto=msg["proto"], sid=msg["sid"],
                                        src=self.stub, sport=msg["sport"],
                                        dst=target.stub, dport=msg["dport"], data=msg["data"]))

        elif cmd == CMD_TCP_CONNECT:
            sid = msg["sid"]
            if sid is None:
                return
            if sid in self.streams or sid in self.pending:
                LOG.warning("流ID 重复使用: %s", sid)
                return
            if len(self.pending) >= MAX_STREAMS:
                # 以前这里是直接把客户端踢下线 —— 结果玩家只是"点了几个进不去的房间"
                # 就被断开重连。挂起流本来就有超时（见 pending_sweeper），超限只需要
                # 拒掉这一条新的，没必要连坐整条连接。
                LOG.warning("挂起流过多（%d），拒绝新流 %s（%s）",
                            len(self.pending), sid, self.keychip)
                await self.send(close_msg(msg["proto"], sid,
                                          target.stub, msg["dport"],
                                          self.stub, msg["sport"],
                                          reason="服务端挂起已满，稍后再试"))
                return
            self.pending[sid] = {"peer": target.stub, "proto": msg["proto"],
                                 "sport": msg["sport"], "dport": msg["dport"],
                                 "ts": time.time()}
            with STATE.lock:
                STATE.stats["connects"] += 1
            await target.send(build_msg(CMD_TCP_CONNECT, proto=msg["proto"], sid=sid,
                                        src=self.stub, sport=msg["sport"],
                                        dst=target.stub, dport=msg["dport"]))

        elif cmd == CMD_TCP_ACCEPT:
            sid = msg["sid"]
            if sid is None or sid not in target.pending:
                # 目标那边已经超时回收了（多半是房主开打了 / 房客自己退了）。
                # 告诉接流方一声，别让它攥着一条服务端并不认的流。
                LOG.warning("接流失败：目标没有挂起该流 %s（多半已超时回收）", sid)
                if sid is not None:
                    await self.send(close_msg(msg["proto"], sid,
                                              target.stub, msg["dport"],
                                              self.stub, msg["sport"],
                                              reason="这条挂起已经回收了"))
                return
            target.pending.pop(sid, None)
            target.streams[sid] = self.stub
            self.streams[sid] = target.stub
            await target.send(build_msg(CMD_TCP_ACCEPT, proto=msg["proto"], sid=sid,
                                        src=self.stub, sport=msg["sport"],
                                        dst=target.stub, dport=msg["dport"]))

        elif cmd == CMD_TCP_CLOSE:
            # ← 原版这里直接忽略，导致流表只增不减
            sid = msg["sid"]
            peer = None
            if sid is not None:
                peer_stub = self.streams.pop(sid, None)
                info = self.pending.pop(sid, None)
                if peer_stub is None and info is not None:
                    peer_stub = info.get("peer")        # 还没 Accept 就取消了
                if peer_stub is not None:
                    with STATE.lock:
                        peer = STATE.clients.get(peer_stub)
                    if peer is not None:
                        peer.streams.pop(sid, None)
                        peer.pending.pop(sid, None)
            LOG.debug("关流 %s（对端 %s）", sid,
                      peer.keychip if peer else "已离线")
            # 转发给对端：以前只清服务端的两张流表就走了，两边的游戏都不知道
            # 这条流已经没了，会一直攥着它继续发数据。
            if peer is not None and peer is not self:
                await peer.send(close_msg(msg["proto"], sid,
                                          self.stub, msg["sport"],
                                          peer.stub, msg["dport"],
                                          reason=msg["data"] or "对端关闭了流"))

        else:
            LOG.debug("未处理的命令: %s", cmd)


async def pending_sweeper(interval: float = 1.0):
    """
    定期回收"没人接"的建流请求。

    原版和第一版都是发出 CTL_TCP_CONNECT 之后就撒手不管：只要对端不 Accept，
    这条挂起记录就永远留着。房客反复点"加入"、或者点进一个已经开打的房间，
    pending 只会涨，涨到 MAX_STREAMS 就被服务端踢下线（玩家看到的是莫名其妙的掉线）。

    回收时给两边都发一条 CTL_TCP_CLOSE：房客那边据此放弃这次连接，
    房主那边据此把排队等 Accept 的请求丢掉。
    """
    while True:
        await asyncio.sleep(interval)
        if STATE is None:
            continue
        now = time.time()
        with STATE.lock:
            clients = list(STATE.clients.values())
        for client in clients:
            for sid, info in list(client.pending.items()):
                waited = now - info["ts"]
                if waited <= PENDING_TIMEOUT:
                    continue
                client.pending.pop(sid, None)
                LOG.info("建流请求 %.0fs 没人接，回收 sid=%s（%s → %s）",
                         waited, sid, client.keychip, stub_to_ip(info["peer"]))
                await client.send(close_msg(info["proto"], sid,
                                            info["peer"], info["dport"],
                                            client.stub, info["sport"],
                                            reason="对方一直没接流，已回收"))
                peer = STATE.clients.get(info["peer"])
                if peer is not None:
                    peer.pending.pop(sid, None)
                    await peer.send(close_msg(info["proto"], sid,
                                              client.stub, info["sport"],
                                              peer.stub, info["dport"],
                                              reason="对方放弃了这条建流"))


async def cancel_pending_to(stub: int, reason: str):
    """房间关了（或房主下线）时，把所有指向它的挂起建流请求一并取消。"""
    if STATE is None:
        return
    with STATE.lock:
        clients = list(STATE.clients.values())
    for client in clients:
        for sid, info in list(client.pending.items()):
            if info.get("peer") != stub:
                continue
            client.pending.pop(sid, None)
            LOG.info("房间没了，取消挂起建流 sid=%s（%s）", sid, reason)
            await client.send(close_msg(info["proto"], sid,
                                        stub, info["dport"],
                                        client.stub, info["sport"],
                                        reason=reason))


async def relay_serve(host: str, port: int, heartbeat_timeout: int,
                      stop: "asyncio.Event | None" = None):
    async def on_client(reader: asyncio.StreamReader, writer: asyncio.StreamWriter):
        peer = writer.get_extra_info("peername") or ("?", 0)
        client = RelayClient(reader, writer, peer[0])
        try:
            while True:
                try:
                    raw = await asyncio.wait_for(reader.readline(), timeout=heartbeat_timeout)
                except asyncio.TimeoutError:
                    STATE.log_event("断开", f"{client.keychip or peer[0]} 心跳超时")
                    break
                if not raw:
                    break
                line = raw.decode("utf-8", "replace").strip()
                if not line:
                    continue
                if line != "1,3":
                    LOG.debug("<< %s %s", client.keychip or peer[0], line[:160])
                try:
                    msg = parse_msg(line)
                except Exception as exc:
                    LOG.warning("消息解析失败: %s", exc)
                    continue
                if msg["cmd"] is None:
                    continue
                try:
                    if msg["cmd"] == CMD_START:
                        await client.register(msg["data"] or "")
                    elif client.stub is None:
                        LOG.warning("未注册就发消息，忽略: %s", line[:80])
                    else:
                        await client.handle(msg)
                except Exception as exc:
                    LOG.exception("处理消息出错: %s", exc)
        except Exception as exc:
            LOG.debug("连接异常: %s", exc)
        finally:
            await client.close()
            if client.stub is not None and STATE is not None:
                with STATE.lock:
                    if STATE.clients.get(client.stub) is client:
                        STATE.clients.pop(client.stub, None)
                STATE.log_event("断开", f"{client.keychip}（{stub_to_ip(client.stub)}）离线")

    server = await asyncio.start_server(on_client, host, port)
    LOG.info("中继已启动：%s:%d", host, port)
    sweeper = asyncio.create_task(pending_sweeper())
    try:
        if stop is None:
            async with server:
                await server.serve_forever()
            return
        # 有 stop 事件时（systemd / Ctrl+C）：等信号，然后把监听关掉正常退出。
        # 注意别只 add_signal_handler 却不 await —— 那样 SIGTERM 会被吞掉，
        # systemd 要等到 TimeoutStopSec 才 SIGKILL。（踩过）
        async with server:
            await stop.wait()
    finally:
        sweeper.cancel()
        try:
            await sweeper
        except asyncio.CancelledError:
            pass
    LOG.info("收到停止信号，中继已关闭")


# ------------------------------------------------------------------ HTTP 大厅
DASHBOARD = """<!doctype html><html lang="zh"><head><meta charset="utf-8">
<title>instorematchd 看板</title><meta name="viewport" content="width=device-width,initial-scale=1">
<style>
body{font:14px/1.6 system-ui,"Microsoft YaHei",sans-serif;background:#16161c;color:#e8e8ef;margin:0;padding:20px}
h1{font-size:18px;margin:0 0 4px}.sub{color:#8b8b9b;margin-bottom:16px}
.cards{display:flex;gap:12px;flex-wrap:wrap;margin-bottom:16px}
.card{background:#20202a;border-radius:10px;padding:12px 16px;min-width:110px}
.card b{display:block;font-size:24px;color:#9db4ff}
table{border-collapse:collapse;width:100%;margin-bottom:16px}
th,td{text-align:left;padding:6px 10px;border-bottom:1px solid #2c2c38;font-size:13px}
th{color:#8b8b9b;font-weight:500}
.ok{color:#7ee787}.warn{color:#ffa657}
h2{font-size:15px;margin:18px 0 6px;color:#c8c8d8}
</style></head><body>
<h1>instorematchd</h1><div class="sub" id="sub">加载中…</div>
<div class="cards" id="cards"></div>
<h2>在线玩家</h2><table id="clients"><thead><tr><th>keychip</th><th>伪IP</th><th>来源</th><th>在线</th><th>空闲</th><th>流</th></tr></thead><tbody></tbody></table>
<h2>当前房间</h2><table id="rooms"><thead><tr><th>房主</th><th>曲目ID</th><th>难度</th><th>存在</th><th>伪IP</th></tr></thead><tbody></tbody></table>
<h2>最近事件</h2><table id="events"><thead><tr><th>时间</th><th>类型</th><th>内容</th></tr></thead><tbody></tbody></table>
<script>
function ts(t){return new Date(t*1000).toLocaleTimeString('zh-CN',{hour12:false})}
async function tick(){
  let d; try{ d=await (await fetch('/api/status'+location.search)).json() }catch(e){ return }
  document.getElementById('sub').textContent =
    `协议 v${d.version} · 已运行 ${Math.floor(d.uptime/60)} 分 ${d.uptime%60} 秒 · 房间有效期 ${d.recruit_ttl}s` +
    (d.masked ? ' · keychip / IP 已打码（看全部：/admin?token=…）' : ' · 管理员视图：全部信息');
  document.getElementById('cards').innerHTML =
    `<div class="card"><b class="${d.online?'ok':''}">${d.online}</b>在线玩家</div>`+
    `<div class="card"><b>${d.rooms}</b>进行中房间</div>`+
    `<div class="card"><b>${d.stats.registered}</b>累计注册</div>`+
    `<div class="card"><b>${d.stats.connects}</b>累计建流</div>`+
    `<div class="card"><b>${d.stats.sends}</b>转发数据</div>`;
  const row=(cells)=>'<tr>'+cells.map(c=>`<td>${c}</td>`).join('')+'</tr>';
  document.querySelector('#clients tbody').innerHTML =
    d.clients.map(c=>row([c.keychip,c.stub,c.peer,c.connected+'s',c.idle+'s',c.streams])).join('')
    || '<tr><td colspan="6">没有人在线</td></tr>';
  document.querySelector('#rooms tbody').innerHTML =
    d.recruits.map(r=>row([r.host||'-',r.music_id||'-',r.difficulty??'-',r.age+'s',r.stub])).join('')
    || '<tr><td colspan="5">没有房间</td></tr>';
  document.querySelector('#events tbody').innerHTML =
    d.events.map(e=>row([ts(e.ts),e.kind,e.text])).join('') || '<tr><td colspan="3">暂无</td></tr>';
}
tick(); setInterval(tick,2000);
</script></body></html>"""


class LobbyHandler(BaseHTTPRequestHandler):
    server_version = "instorematchd"
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):
        LOG.debug("HTTP %s - %s", self.address_string(), fmt % args)

    # -------------------------------------------------- 工具
    def _send(self, code: int, body: bytes, ctype="application/json; charset=utf-8"):
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Access-Control-Allow-Headers", "Content-Type")
        self.send_header("Access-Control-Allow-Methods", "GET,POST,OPTIONS")
        self.end_headers()
        self.wfile.write(body)

    def _json(self, code: int, obj):
        self._send(code, json.dumps(obj, ensure_ascii=False).encode("utf-8"))

    def _body(self) -> dict:
        try:
            n = int(self.headers.get("Content-Length") or 0)
            raw = self.rfile.read(n) if n else b""
            return json.loads(raw.decode("utf-8")) if raw else {}
        except Exception:
            return {}

    # -------------------------------------------------- 路由
    def do_OPTIONS(self):
        self._send(204, b"")

    def do_POST(self):
        path = urlparse(self.path).path
        data = self._body()
        if path == "/recruit/start":
            self._recruit_start(data)
        elif path == "/recruit/finish":
            self._recruit_finish(data)
        else:
            self._json(404, {"error": "not found"})

    def do_GET(self):
        path = urlparse(self.path).path
        if path == "/recruit/list":
            STATE.prune_recruits()
            with STATE.lock:
                lines = []
                for rec in STATE.recruits.values():
                    item = json.loads(json.dumps(rec["rec"]))
                    item.pop("Keychip", None)
                    item.pop("Time", None)
                    lines.append(json.dumps(item, ensure_ascii=False))
            self._send(200, "\n".join(lines).encode("utf-8"),
                       "text/plain; charset=utf-8")
        elif path == "/info":
            self._json(200, {"relayHost": self._relay_host(), "relayPort": RELAY_PORT})
        elif path == "/online":
            snap = STATE.snapshot()
            self._json(200, {"totalUsers": snap["online"],
                             "activeRecruits": snap["rooms"]})
        elif path == "/api/status":
            # 不带 token → 脱敏视图；带对 token → 全量
            self._json(200, STATE.snapshot(mask=not self._token_ok()))
        elif path == "/admin":
            if not self._token_ok():
                if not ADMIN_TOKEN:
                    self._send(403, "管理员视图没开启：启动时加 --admin-token <token>"
                                    "（或设环境变量 IMD_ADMIN_TOKEN）".encode("utf-8"),
                               "text/plain; charset=utf-8")
                else:
                    self._send(403, "token 不对。用法：/admin?token=你的token".encode("utf-8"),
                               "text/plain; charset=utf-8")
                return
            self._send(200, DASHBOARD.encode("utf-8"), "text/html; charset=utf-8")
        elif path == "/debug":
            if not self._token_ok():
                self._json(403, {"error": "需要 token（/debug?token=… 或 X-Admin-Token 头）"})
                return
            snap = STATE.snapshot()
            snap["host_header"] = self.headers.get("Host")
            self._json(200, snap)
        elif path in ("/", "/index.html"):
            self._send(200, DASHBOARD.encode("utf-8"), "text/html; charset=utf-8")
        else:
            self._json(404, {"error": "not found"})

    def _token_ok(self) -> bool:
        """带对 token 才算管理员：支持 ?token=xxx 或 X-Admin-Token 头"""
        if not ADMIN_TOKEN:
            return False
        tok = (parse_qs(urlparse(self.path).query).get("token") or [""])[0]
        if not tok:
            tok = self.headers.get("X-Admin-Token", "")
        return hmac.compare_digest(tok, ADMIN_TOKEN)

    def _relay_host(self) -> str:
        if HOST_OVERRIDE:
            return HOST_OVERRIDE
        host = self.headers.get("Host") or "127.0.0.1"
        return host.split(":")[0]

    # -------------------------------------------------- 招募
    def _recruit_start(self, data: dict):
        info = (data or {}).get("RecruitInfo") or {}
        mecha = info.get("MechaInfo") or {}
        stub = mecha.get("IpAddress")
        if stub is None:
            self._json(400, {"error": "RecruitInfo.MechaInfo.IpAddress 缺失"})
            return
        stub = int(stub)

        keychip = (data or {}).get("Keychip")
        if not keychip:
            self._json(400, {"error": "缺少 Keychip"})
            return
        # 房间的"伪 IP"必须**就是这个 keychip 算出来的那个**。否则房客拿着列表里的
        # 地址找过来必然是"目标不在线"，而日志里只有一句 warning —— 客户端以前那个
        # "刷卡前用占位 keychip 算过一次伪 IP"的坑正好会踩中，会变成查不出来的失踪案。
        if keychip_to_stub(keychip) != stub:
            LOG.warning("开房被拒：Keychip 与 IpAddress 对不上（%s vs %s）",
                        keychip, stub_to_ip(stub))
            self._json(400, {"error": "Keychip 与 RecruitInfo.MechaInfo.IpAddress 不一致"})
            return

        # /recruit/start 是个公开接口（谁都能 POST），加一道按来源 IP 的限速和一个总量上限
        now = time.time()
        src = self.client_address[0]
        with STATE.lock:
            posts = STATE.room_posts.setdefault(src, deque())
            while posts and now - posts[0] > ROOM_RATE_WINDOW:
                posts.popleft()
            rate_limited = len(posts) >= ROOM_RATE_MAX
            if not rate_limited:
                posts.append(now)
                room_full = stub not in STATE.recruits and len(STATE.recruits) >= MAX_ROOMS
            else:
                room_full = False
        if rate_limited:
            LOG.warning("开房被限速：%s（%.0fs 内超过 %d 次）", src, ROOM_RATE_WINDOW, ROOM_RATE_MAX)
            self._json(429, {"error": "开房太频繁，稍后再试"})
            return
        if room_full:
            self._json(429, {"error": "大厅房间已满，稍后再试"})
            return

        with STATE.lock:
            is_new = stub not in STATE.recruits
            STATE.recruits[stub] = {"rec": data, "keychip": keychip, "ts": now}
        if is_new:
            names = mecha.get("UserNames") or []
            STATE.log_event("开房", f"{names[0] if names else '?'} 开房"
                                    f"（曲目 {info.get('MusicID') or mecha.get('MusicID')}）")
        else:
            LOG.debug("刷新房间 %s（%s）", stub_to_ip(stub), keychip)
        self._json(200, {"ok": True})

    def _recruit_finish(self, data: dict):
        info = (data or {}).get("RecruitInfo") or {}
        mecha = info.get("MechaInfo") or {}
        stub = mecha.get("IpAddress")
        if stub is None:
            self._json(400, {"error": "缺少 IpAddress"})
            return
        stub = int(stub)
        with STATE.lock:
            rec = STATE.recruits.get(stub)
            if rec is None:
                self._json(404, {"error": "没有这个房间"})
                return
            keychip = (data or {}).get("Keychip")
            if rec["keychip"] and keychip != rec["keychip"]:
                self._json(403, {"error": "Keychip 不匹配"})
                return
            STATE.recruits.pop(stub, None)
        names = mecha.get("UserNames") or []
        STATE.log_event("关房", f"{names[0] if names else '?'} 结束招募")
        # 房主开打了/退了，可能还有人正挂着"建流"等他接 —— 一起取消掉，
        # 否则那些人要干等到 pending 超时（表现就是"点了加入一直转圈"）。
        schedule(cancel_pending_to(stub, "房主结束了招募"))
        self._json(200, {"ok": True})


HOST_OVERRIDE = ""
RELAY_PORT = 20101
LOOP: "asyncio.AbstractEventLoop | None" = None      # 中继跑在哪个事件循环上（见 schedule）


def schedule(coro):
    """
    把协程丢回中继的事件循环去执行。

    大厅是 ThreadingHTTPServer（每个请求一个线程），而中继跑在主线程的 asyncio
    loop 上 —— 在 HTTP 线程里没法直接 await。关房时要取消别人的挂起建流，
    就必须走这一道。
    """
    if LOOP is None or LOOP.is_closed():
        return
    try:
        asyncio.run_coroutine_threadsafe(coro, LOOP)
    except RuntimeError:
        pass


def lobby_serve(bind: str, port: int):
    httpd = ThreadingHTTPServer((bind, port), LobbyHandler)
    httpd.daemon_threads = True
    LOG.info("大厅已启动：%s:%d", bind, port)
    httpd.serve_forever()


# ------------------------------------------------------------------ 入口
def main():
    global STATE, HOST_OVERRIDE, RELAY_PORT, ADMIN_TOKEN, LOOP
    global PENDING_TIMEOUT, MAX_ROOMS

    ap = argparse.ArgumentParser(description="兼容 WorldLink/NyanLink 的联机服务端")
    ap.add_argument("--bind", default="0.0.0.0", help="监听地址（默认 0.0.0.0）")
    ap.add_argument("--lobby-port", type=int, default=20100)
    ap.add_argument("--relay-port", type=int, default=20101)
    ap.add_argument("--host-override", default="",
                    help="强制 /info 返回的中继主机名（走反代时必填，例如 maimai.example.com）")
    ap.add_argument("--recruit-ttl", type=int, default=30,
                    help="房间在这些秒内没有刷新就自动消失（默认 30）")
    ap.add_argument("--heartbeat-timeout", type=int, default=30,
                    help="多久没收到心跳就断开（默认 30 秒）")
    ap.add_argument("--pending-timeout", type=int, default=PENDING_TIMEOUT,
                    help="建流请求多久没人接就回收、并回一条 CTL_TCP_CLOSE 给请求方"
                         "（默认 10 秒）")
    ap.add_argument("--max-rooms", type=int, default=MAX_ROOMS,
                    help="大厅同时在册的房间上限（默认 200）")
    ap.add_argument("--log-level", default="INFO",
                    choices=["DEBUG", "INFO", "WARNING", "ERROR"])
    ap.add_argument("--admin-token", default=os.environ.get("IMD_ADMIN_TOKEN", ""),
                    help="看 /admin（完整信息）用的 token；也可以放进环境变量 "
                         "IMD_ADMIN_TOKEN。不设置就不开放 /admin，"
                         "公开看板的 keychip / IP 依旧打码")
    args = ap.parse_args()

    logging.basicConfig(
        level=getattr(logging, args.log_level),
        format="%(asctime)s %(levelname)-7s %(message)s",
        datefmt="%m-%d %H:%M:%S",
    )

    RELAY_PORT = args.relay_port
    HOST_OVERRIDE = args.host_override
    ADMIN_TOKEN = args.admin_token or ""
    PENDING_TIMEOUT = args.pending_timeout
    MAX_ROOMS = args.max_rooms
    LOG.info("管理员视图(/admin)：%s", "已开启" if ADMIN_TOKEN else "未开启")
    LOG.info("建流挂起超时 %ds，房间上限 %d", PENDING_TIMEOUT, MAX_ROOMS)
    STATE = State(args.recruit_ttl)

    threading.Thread(target=lobby_serve, args=(args.bind, args.lobby_port),
                     daemon=True).start()

    loop = asyncio.new_event_loop()
    asyncio.set_event_loop(loop)
    LOOP = loop

    stop = asyncio.Event()
    for sig in (signal.SIGINT, signal.SIGTERM):
        try:
            loop.add_signal_handler(sig, stop.set)
        except NotImplementedError:
            pass

    try:
        loop.run_until_complete(relay_serve(args.bind, args.relay_port,
                                            args.heartbeat_timeout, stop))
    except (KeyboardInterrupt, SystemExit):
        pass
    finally:
        LOG.info("已停止")


if __name__ == "__main__":
    sys.exit(main())
