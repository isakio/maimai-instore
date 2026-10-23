#!/usr/bin/env python3
"""
instorematchd —— 兼容 InStoreLink / WorldLink / NyanLink 的大厅 + 中继。

只依赖 Python 标准库：
  HTTP :20100  /recruit/start /recruit/finish /recruit/list /info /online
               /api/status /admin /debug /
  TCP  :20101  一行一条消息，字段用逗号分隔

协议与上游逐字节兼容，客户端不需要因为本服务端改动而改协议。
"""

from __future__ import annotations

import argparse
import asyncio
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
ADMIN_TOKEN = ""

_KEYCHIP_RE = re.compile(r"\b[AW][0-9A-Za-z][0-9A-Za-z-]{5,}\b")
_IP_RE = re.compile(r"\b(?:\d{1,3}\.){3}\d{1,3}\b")


def mask_keychip(value):
    if not value:
        return value
    s = str(value)
    if len(s) <= 8:
        return s[:2] + "***"
    return s[:5] + "*" * (len(s) - 8) + s[-3:]


def mask_ip(value):
    if not value:
        return value
    parts = str(value).split(".")
    if len(parts) == 4:
        return f"{parts[0]}.{parts[1]}.x.x"
    return str(value)[:4] + "***"


def mask_text(text):
    if not text:
        return text

    def repl(match):
        value = match.group(0)
        return mask_keychip(value) if sum(ch.isdigit() for ch in value) >= 4 else value

    return _IP_RE.sub(lambda m: mask_ip(m.group(0)), _KEYCHIP_RE.sub(repl, text))


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

MAX_BODY_BYTES = 64 * 1024
MAX_STREAMS = 10
MAX_ESTABLISHED_STREAMS = 64
PENDING_TIMEOUT = 10
MAX_ROOMS = 200
ROOM_RATE_WINDOW = 10.0
ROOM_RATE_MAX = 20
MAX_CLIENTS = 2000
MAX_CLIENTS_PER_IP = 256
RELAY_LINE_LIMIT = 1024 * 1024
SEND_DRAIN_TIMEOUT = 10
LOBBY_TIMEOUT = 15
MAX_LOBBY_THREADS = 64
BROADCAST_RATE_MAX = 10

CMD_NAMES = {
    CMD_START: "CTL_START",
    CMD_HEARTBEAT: "CTL_HEARTBEAT",
    CMD_TCP_CONNECT: "CTL_TCP_CONNECT",
    CMD_TCP_ACCEPT: "CTL_TCP_ACCEPT",
    CMD_TCP_CLOSE: "CTL_TCP_CLOSE",
    CMD_SEND: "DATA_SEND",
    CMD_BROADCAST: "DATA_BROADCAST",
}


def keychip_to_stub(keychip: str) -> int:
    d = hashlib.md5(keychip.encode("utf-8")).digest()
    return (d[0] << 24) | (d[1] << 16) | (d[2] << 8) | d[3]


def stub_to_ip(stub: int) -> str:
    return f"{stub >> 24}.{(stub >> 16) & 255}.{(stub >> 8) & 255}.{stub & 255}"


def as_stub(value):
    if isinstance(value, bool) or not isinstance(value, int):
        return None
    return value


def as_dict(value) -> dict:
    return value if isinstance(value, dict) else {}


def first_name(mecha) -> "str | None":
    names = mecha.get("UserNames") if isinstance(mecha, dict) else None
    if isinstance(names, list) and names:
        return names[0]
    return None


def first_of(value):
    return value[0] if isinstance(value, list) and value else None


def rec_parts(rec: dict):
    info = as_dict(rec.get("RecruitInfo"))
    return info, as_dict(info.get("MechaInfo"))


def parse_msg(line: str) -> dict:
    f = line.split(",")

    def num(index, cast=int):
        if index < len(f) and f[index] != "":
            try:
                return cast(f[index])
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
    arr = [1, cmd, proto, sid, src, sport, dst, dport,
           None, None, None, None, None, None, None, None, data]
    return ",".join("" if x is None else str(x) for x in arr).rstrip(",")


def ctl(cmd, data=None) -> str:
    return build_msg(cmd, data=data)


def close_msg(proto, sid, src_stub, src_port, dst_stub, dst_port, reason=None) -> str:
    # 收件人视角：dst 是收件人，dport 是收件人自己的端口。
    return build_msg(CMD_TCP_CLOSE, proto=proto, sid=sid,
                     src=src_stub, sport=src_port,
                     dst=dst_stub, dport=dst_port, data=reason)


class State:
    def __init__(self, recruit_ttl: int):
        self.lock = threading.Lock()
        self.clients: dict[int, RelayClient] = {}
        self.recruits: dict[int, dict] = {}
        self.room_posts: dict[str, deque] = {}
        self.events = deque(maxlen=200)
        self.started_at = time.time()
        self.recruit_ttl = recruit_ttl
        self.stats = {"registered": 0, "sends": 0, "broadcasts": 0, "connects": 0}

    def log_event(self, kind: str, text: str):
        with self.lock:
            self.events.appendleft({"ts": time.time(), "kind": kind, "text": text})
        LOG.info("[%s] %s", kind, text)

    def prune_rooms(self):
        now = time.time()
        with self.lock:
            for stub, rec in list(self.recruits.items()):
                if now - rec["ts"] > self.recruit_ttl:
                    self.recruits.pop(stub, None)
            for stub, rec in list(self.recruits.items()):
                keychip = rec.get("keychip")
                if keychip and keychip_to_stub(keychip) not in self.clients:
                    self.recruits.pop(stub, None)
            for src, posts in list(self.room_posts.items()):
                if not posts or now - posts[-1] > ROOM_RATE_WINDOW:
                    self.room_posts.pop(src, None)

    def snapshot(self, mask: bool = False) -> dict:
        self.prune_rooms()
        with self.lock:
            clients = [{
                "keychip": c.keychip,
                "stub": stub_to_ip(c.stub) if c.stub else None,
                "peer": c.peer_ip,
                "connected": round(time.time() - c.connected_at),
                "idle": round(time.time() - c.last_heartbeat, 1),
                "streams": len(c.streams),
            } for c in self.clients.values()]
            recruits = []
            for stub, rec in self.recruits.items():
                info, mecha = rec_parts(rec["rec"])
                recruits.append({
                    "host": first_name(mecha),
                    "music_id": info.get("MusicID") or mecha.get("MusicID"),
                    "difficulty": first_of(mecha.get("FumenDifs")),
                    "age": round(time.time() - rec["ts"], 1),
                    "stub": stub_to_ip(stub),
                })
            events = list(self.events)[:40]
            stats = dict(self.stats)

        if mask:
            for c in clients:
                c["keychip"] = mask_keychip(c["keychip"])
                c["stub"] = mask_ip(c["stub"])
                c["peer"] = mask_ip(c["peer"])
            for r in recruits:
                r["stub"] = mask_ip(r["stub"])
            events = [{"ts": e["ts"], "kind": e["kind"],
                       "text": mask_text(e["text"])} for e in events]

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


class RelayClient:
    def __init__(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter,
                 peer_ip: str):
        self.reader = reader
        self.writer = writer
        self.peer_ip = peer_ip
        self.keychip: str | None = None
        self.stub: int | None = None
        self.streams: dict[int, int] = {}
        self.pending: dict[int, dict] = {}
        self.last_heartbeat = time.time()
        self.connected_at = time.time()
        self.unregistered = 0
        self.oversize = 0
        self.broadcast_times = deque()
        self.broadcast_dropped = 0
        self._lock = asyncio.Lock()
        self.closed = False

    async def send(self, line: str):
        if self.closed:
            return
        async with self._lock:
            try:
                self.writer.write((line + "\n").encode("utf-8"))
                await asyncio.wait_for(self.writer.drain(), timeout=SEND_DRAIN_TIMEOUT)
            except asyncio.TimeoutError:
                LOG.warning("发送缓冲排不空，断开 %s", self.keychip or self.peer_ip)
                transport = getattr(self.writer, "transport", None)
                if transport is not None:
                    try:
                        transport.abort()
                    except Exception:
                        pass
                await self.close()
            except Exception:
                await self.close()

    async def close(self):
        if self.closed:
            return
        self.closed = True
        try:
            self.writer.close()
        except Exception:
            pass

    async def register(self, keychip: str) -> bool:
        if not keychip or STATE is None:
            return False
        stub = keychip_to_stub(keychip)
        with STATE.lock:
            old = STATE.clients.get(stub)
            if old is not None and old is not self:
                asyncio.create_task(old.close())
            if self.stub is not None and self.stub != stub:
                if STATE.clients.get(self.stub) is self:
                    STATE.clients.pop(self.stub, None)
            if old is None:
                same_ip = sum(1 for c in STATE.clients.values()
                              if c is not self and c.peer_ip == self.peer_ip)
                if len(STATE.clients) >= MAX_CLIENTS or same_ip >= MAX_CLIENTS_PER_IP:
                    return False
            self.keychip = keychip
            self.stub = stub
            STATE.clients[stub] = self
            STATE.stats["registered"] += 1
        STATE.log_event("注册", f"{keychip} 来自 {self.peer_ip} → {stub_to_ip(stub)}")
        await self.send(ctl(CMD_START, data=f"version={PROTO_VERSION}"))
        return True

    def find_target(self, msg: dict):
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
                return
            now = time.time()
            while self.broadcast_times and now - self.broadcast_times[0] > ROOM_RATE_WINDOW:
                self.broadcast_times.popleft()
            if len(self.broadcast_times) >= BROADCAST_RATE_MAX:
                self.broadcast_dropped += 1
                return
            self.broadcast_times.append(now)
            with STATE.lock:
                targets = list(STATE.clients.values())
                STATE.stats["broadcasts"] += 1
            for target in targets:
                await target.send(build_msg(
                    CMD_BROADCAST, proto=msg["proto"], sid=msg["sid"],
                    src=self.stub, sport=msg["sport"], dst=msg["dst"],
                    dport=msg["dport"], data=msg["data"]))
            return

        if cmd == CMD_TCP_CLOSE:
            await self.handle_close(msg)
            return

        target = self.find_target(msg)
        if target is None:
            if cmd in (CMD_TCP_CONNECT, CMD_TCP_ACCEPT, CMD_SEND):
                if msg["sid"] is not None and msg["dst"] is not None:
                    await self.send(close_msg(
                        msg["proto"], msg["sid"], msg["dst"], msg["dport"],
                        self.stub, msg["sport"], reason="目标不在线"))
            return

        if cmd == CMD_SEND:
            if (msg["proto"] == PROTO_TCP and msg["sid"] is not None
                    and msg["sid"] not in self.streams):
                return
            with STATE.lock:
                STATE.stats["sends"] += 1
            await target.send(build_msg(
                CMD_SEND, proto=msg["proto"], sid=msg["sid"],
                src=self.stub, sport=msg["sport"], dst=target.stub,
                dport=msg["dport"], data=msg["data"]))
            return

        if cmd == CMD_TCP_CONNECT:
            sid = msg["sid"]
            if sid is None or sid in self.streams or sid in self.pending:
                return
            if len(self.pending) >= MAX_STREAMS:
                await self.send(close_msg(
                    msg["proto"], sid, target.stub, msg["dport"],
                    self.stub, msg["sport"], reason="服务端挂起已满，稍后再试"))
                return
            self.pending[sid] = {
                "peer": target.stub, "proto": msg["proto"],
                "sport": msg["sport"], "dport": msg["dport"], "ts": time.time(),
            }
            with STATE.lock:
                STATE.stats["connects"] += 1
            await target.send(build_msg(
                CMD_TCP_CONNECT, proto=msg["proto"], sid=sid,
                src=self.stub, sport=msg["sport"], dst=target.stub,
                dport=msg["dport"]))
            return

        if cmd == CMD_TCP_ACCEPT:
            sid = msg["sid"]
            if sid is None or sid not in target.pending:
                if sid is not None:
                    await self.send(close_msg(
                        msg["proto"], sid, target.stub, msg["dport"],
                        self.stub, msg["sport"], reason="这条挂起已经回收了"))
                return
            if target.pending[sid].get("peer") != self.stub:
                await self.send(close_msg(
                    msg["proto"], sid, target.stub, msg["dport"],
                    self.stub, msg["sport"], reason="这条挂起不是你的"))
                return
            target.pending.pop(sid, None)
            if (len(target.streams) >= MAX_ESTABLISHED_STREAMS
                    or len(self.streams) >= MAX_ESTABLISHED_STREAMS):
                reason = "这个客户端的流数已达上限"
                await self.send(close_msg(
                    msg["proto"], sid, target.stub, msg["dport"],
                    self.stub, msg["sport"], reason=reason))
                if target is not self:
                    await target.send(close_msg(
                        msg["proto"], sid, self.stub, msg["sport"],
                        target.stub, msg["dport"], reason=reason))
                return
            target.streams[sid] = self.stub
            self.streams[sid] = target.stub
            await target.send(build_msg(
                CMD_TCP_ACCEPT, proto=msg["proto"], sid=sid,
                src=self.stub, sport=msg["sport"], dst=target.stub,
                dport=msg["dport"]))

    async def handle_close(self, msg: dict):
        sid = msg["sid"]
        peer = None
        if sid is not None:
            peer_stub = self.streams.pop(sid, None)
            info = self.pending.pop(sid, None)
            if peer_stub is None and info is not None:
                peer_stub = info.get("peer")
            if peer_stub is not None and STATE is not None:
                with STATE.lock:
                    peer = STATE.clients.get(peer_stub)
                if peer is not None:
                    peer.streams.pop(sid, None)
                    peer.pending.pop(sid, None)
        if peer is not None and peer is not self:
            await peer.send(close_msg(
                msg["proto"], sid, self.stub, msg["sport"],
                peer.stub, msg["dport"], reason=msg["data"] or "对端关闭了流"))


async def pending_sweeper():
    while True:
        await asyncio.sleep(1.0)
        if STATE is None:
            continue
        now = time.time()
        with STATE.lock:
            clients = list(STATE.clients.values())
        for client in clients:
            for sid, info in list(client.pending.items()):
                if now - info["ts"] <= PENDING_TIMEOUT:
                    continue
                client.pending.pop(sid, None)
                await client.send(close_msg(
                    info["proto"], sid, info["peer"], info["dport"],
                    client.stub, info["sport"],
                    reason="对方一直没接流，已回收"))
                with STATE.lock:
                    peer = STATE.clients.get(info["peer"])
                if peer is not None:
                    peer.pending.pop(sid, None)
                    await peer.send(close_msg(
                        info["proto"], sid, client.stub, info["sport"],
                        peer.stub, info["dport"], reason="对方放弃了这条建流"))


async def cancel_pending_to(stub: int, reason: str):
    if STATE is None:
        return
    with STATE.lock:
        clients = list(STATE.clients.values())
    for client in clients:
        for sid, info in list(client.pending.items()):
            if info.get("peer") != stub:
                continue
            client.pending.pop(sid, None)
            await client.send(close_msg(
                info["proto"], sid, stub, info["dport"],
                client.stub, info["sport"], reason=reason))


async def relay_serve(host: str, port: int, heartbeat_timeout: int,
                      stop: "asyncio.Event | None" = None):
    async def cleanup(client: RelayClient):
        if client.stub is None or STATE is None:
            return
        with STATE.lock:
            others = [c for c in STATE.clients.values() if c is not client]
        orphan_pending = []
        for other in others:
            for sid in [s for s, peer in other.streams.items() if peer == client.stub]:
                other.streams.pop(sid, None)
            for sid, info in list(other.pending.items()):
                if info.get("peer") == client.stub:
                    other.pending.pop(sid, None)
                    orphan_pending.append((other, sid, info))
        for other, sid, info in orphan_pending:
            await other.send(close_msg(
                info["proto"], sid, client.stub, info["dport"],
                other.stub, info["sport"], reason="对方已离线"))
        with STATE.lock:
            if STATE.clients.get(client.stub) is client:
                STATE.clients.pop(client.stub, None)
        STATE.log_event("断开", f"{client.keychip or client.peer_ip} 离线")

    async def on_client(reader: asyncio.StreamReader, writer: asyncio.StreamWriter):
        peer = writer.get_extra_info("peername") or ("?", 0)
        client = RelayClient(reader, writer, peer[0])
        try:
            while True:
                try:
                    raw = await asyncio.wait_for(
                        reader.readline(), timeout=heartbeat_timeout)
                except asyncio.TimeoutError:
                    break
                except ValueError:
                    client.oversize += 1
                    if client.oversize <= 3:
                        LOG.warning("单行超过 %d 字节，已跳过（来源 %s）",
                                    RELAY_LINE_LIMIT, client.keychip or peer[0])
                    continue
                if not raw:
                    break
                line = raw.decode("utf-8", "replace").strip()
                if not line:
                    continue
                try:
                    msg = parse_msg(line)
                except Exception:
                    continue
                if msg["cmd"] is None:
                    continue
                try:
                    if msg["cmd"] == CMD_START:
                        if not await client.register(msg["data"] or ""):
                            break
                    elif client.stub is None:
                        client.unregistered += 1
                        if client.unregistered <= 3:
                            LOG.warning("未注册就发消息，忽略（来源 %s）", peer[0])
                        else:
                            break
                    else:
                        await client.handle(msg)
                except Exception:
                    LOG.exception("处理消息出错")
        except Exception:
            LOG.debug("连接异常", exc_info=True)
        finally:
            await client.close()
            await cleanup(client)

    server = await asyncio.start_server(on_client, host, port, limit=RELAY_LINE_LIMIT)
    LOG.info("中继已启动：%s:%d", host, port)
    sweeper = asyncio.create_task(pending_sweeper())
    try:
        if stop is None:
            async with server:
                await server.serve_forever()
        else:
            async with server:
                await stop.wait()
    finally:
        sweeper.cancel()
        try:
            await sweeper
        except asyncio.CancelledError:
            pass
    LOG.info("收到停止信号，中继已关闭")


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
.ok{color:#7ee787}
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
    `协议 v${d.version} · 已运行 ${Math.floor(d.uptime/60)} 分 ${d.uptime%60} 秒` +
    (d.masked ? ' · keychip / IP 已打码' : ' · 管理员视图');
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

    def _send(self, code: int, body: bytes, ctype="application/json; charset=utf-8"):
        try:
            self.send_response(code)
            self.send_header("Content-Type", ctype)
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Access-Control-Allow-Origin", "*")
            self.send_header("Access-Control-Allow-Headers", "Content-Type")
            self.send_header("Access-Control-Allow-Methods", "GET,POST,OPTIONS")
            self.end_headers()
            self.wfile.write(body)
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError,
                TimeoutError, OSError):
            self.close_connection = True

    def _json(self, code: int, obj):
        self._send(code, json.dumps(obj, ensure_ascii=False).encode("utf-8"))

    def _body(self) -> dict:
        try:
            n = int(self.headers.get("Content-Length") or 0)
            if n <= 0:
                return {}
            if n > MAX_BODY_BYTES:
                self.close_connection = True
                return {}
            raw = self.rfile.read(n)
            if not raw:
                return {}
            data = json.loads(raw.decode("utf-8"))
            return data if isinstance(data, dict) else {}
        except Exception:
            return {}

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
            STATE.prune_rooms()
            with STATE.lock:
                lines = []
                for rec in STATE.recruits.values():
                    item = json.loads(json.dumps(rec["rec"]))
                    item.pop("Keychip", None)
                    item.pop("Time", None)
                    lines.append(json.dumps(item, ensure_ascii=False))
            self._send(200, "\n".join(lines).encode("utf-8"), "text/plain; charset=utf-8")
        elif path == "/info":
            self._json(200, {"relayHost": self._relay_host(), "relayPort": RELAY_PORT})
        elif path == "/online":
            snap = STATE.snapshot()
            self._json(200, {"totalUsers": snap["online"], "activeRecruits": snap["rooms"]})
        elif path == "/api/status":
            self._json(200, STATE.snapshot(mask=not self._token_ok()))
        elif path == "/admin":
            if not self._token_ok():
                self._send(403, b"forbidden", "text/plain; charset=utf-8")
                return
            self._send(200, DASHBOARD.encode("utf-8"), "text/html; charset=utf-8")
        elif path == "/debug":
            if not self._token_ok():
                self._json(403, {"error": "forbidden"})
                return
            snap = STATE.snapshot()
            snap["host_header"] = self.headers.get("Host")
            self._json(200, snap)
        elif path in ("/", "/index.html"):
            self._send(200, DASHBOARD.encode("utf-8"), "text/html; charset=utf-8")
        else:
            self._json(404, {"error": "not found"})

    def _token_ok(self) -> bool:
        if not ADMIN_TOKEN:
            return False
        tok = (parse_qs(urlparse(self.path).query).get("token") or [""])[0]
        if not tok:
            tok = self.headers.get("X-Admin-Token", "")
        try:
            return hmac.compare_digest(tok.encode("utf-8"), ADMIN_TOKEN.encode("utf-8"))
        except Exception:
            return False

    def _relay_host(self) -> str:
        if HOST_OVERRIDE:
            return HOST_OVERRIDE
        return (self.headers.get("Host") or "127.0.0.1").split(":")[0]

    def _recruit_start(self, data: dict):
        info = as_dict((data or {}).get("RecruitInfo"))
        if not info:
            self._json(400, {"error": "RecruitInfo 缺失或不是对象"})
            return
        mecha = as_dict(info.get("MechaInfo"))
        if not mecha:
            self._json(400, {"error": "MechaInfo 缺失或不是对象"})
            return
        stub = as_stub(mecha.get("IpAddress"))
        if stub is None:
            self._json(400, {"error": "IpAddress 缺失或不是整数"})
            return
        keychip = (data or {}).get("Keychip")
        if not isinstance(keychip, str) or not keychip:
            self._json(400, {"error": "缺少 Keychip（或它不是字符串）"})
            return
        if keychip_to_stub(keychip) != stub:
            self._json(400, {"error": "Keychip 与 IpAddress 不一致"})
            return

        now = time.time()
        src = self.client_address[0]
        with STATE.lock:
            posts = STATE.room_posts.setdefault(src, deque())
            while posts and now - posts[0] > ROOM_RATE_WINDOW:
                posts.popleft()
            if len(posts) >= ROOM_RATE_MAX:
                self._json(429, {"error": "开房太频繁，稍后再试"})
                return
            posts.append(now)
            if stub not in STATE.recruits and len(STATE.recruits) >= MAX_ROOMS:
                self._json(429, {"error": "大厅房间已满，稍后再试"})
                return
            is_new = stub not in STATE.recruits
            STATE.recruits[stub] = {"rec": data, "keychip": keychip, "ts": now}
        if is_new:
            STATE.log_event("开房", f"{first_name(mecha) or '?'} 开房"
                                    f"（曲目 {info.get('MusicID') or mecha.get('MusicID')}）")
        self._json(200, {"ok": True})

    def _recruit_finish(self, data: dict):
        info = as_dict((data or {}).get("RecruitInfo"))
        if not info:
            self._json(400, {"error": "RecruitInfo 缺失或不是对象"})
            return
        mecha = as_dict(info.get("MechaInfo"))
        if not mecha:
            self._json(400, {"error": "MechaInfo 缺失或不是对象"})
            return
        stub = as_stub(mecha.get("IpAddress"))
        if stub is None:
            self._json(400, {"error": "IpAddress 缺失或不是整数"})
            return
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
        STATE.log_event("关房", f"{first_name(mecha) or '?'} 结束招募")
        schedule(cancel_pending_to(stub, "房主结束了招募"))
        self._json(200, {"ok": True})


HOST_OVERRIDE = ""
RELAY_PORT = 20101
LOOP: "asyncio.AbstractEventLoop | None" = None


def schedule(coro):
    if LOOP is None or LOOP.is_closed():
        return
    try:
        asyncio.run_coroutine_threadsafe(coro, LOOP)
    except RuntimeError:
        pass


class BoundedThreadingHTTPServer(ThreadingHTTPServer):
    daemon_threads = True
    request_queue_size = 128

    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self._sema = threading.Semaphore(MAX_LOBBY_THREADS)

    def process_request(self, request, client_address):
        self._sema.acquire()
        try:
            super().process_request(request, client_address)
        except BaseException:
            self._sema.release()
            raise

    def shutdown_request(self, request):
        try:
            super().shutdown_request(request)
        finally:
            self._sema.release()


def lobby_serve(bind: str, port: int):
    LobbyHandler.timeout = LOBBY_TIMEOUT
    httpd = BoundedThreadingHTTPServer((bind, port), LobbyHandler)
    httpd.daemon_threads = True
    LOG.info("大厅已启动：%s:%d（最多 %d 个请求线程）",
             bind, port, MAX_LOBBY_THREADS)
    httpd.serve_forever()


def main():
    global STATE, HOST_OVERRIDE, RELAY_PORT, ADMIN_TOKEN, LOOP
    global PENDING_TIMEOUT, MAX_ROOMS, MAX_CLIENTS, MAX_CLIENTS_PER_IP
    global SEND_DRAIN_TIMEOUT, LOBBY_TIMEOUT, MAX_LOBBY_THREADS

    def positive(name):
        def parse(text):
            try:
                n = int(text)
            except ValueError:
                raise argparse.ArgumentTypeError(f"{name} 必须是整数")
            if n < 1:
                raise argparse.ArgumentTypeError(f"{name} 必须 >= 1")
            return n
        return parse

    ap = argparse.ArgumentParser(description="InStoreLink 大厅 + 中继")
    ap.add_argument("--bind", default="0.0.0.0")
    ap.add_argument("--lobby-port", type=positive("--lobby-port"), default=20100)
    ap.add_argument("--relay-port", type=positive("--relay-port"), default=20101)
    ap.add_argument("--host-override", default="")
    ap.add_argument("--recruit-ttl", type=positive("--recruit-ttl"), default=30)
    ap.add_argument("--heartbeat-timeout", type=positive("--heartbeat-timeout"), default=30)
    ap.add_argument("--pending-timeout", type=positive("--pending-timeout"), default=PENDING_TIMEOUT)
    ap.add_argument("--max-rooms", type=positive("--max-rooms"), default=MAX_ROOMS)
    ap.add_argument("--max-clients", type=positive("--max-clients"), default=MAX_CLIENTS)
    ap.add_argument("--max-clients-per-ip", type=positive("--max-clients-per-ip"),
                    default=MAX_CLIENTS_PER_IP)
    ap.add_argument("--send-timeout", type=positive("--send-timeout"),
                    default=SEND_DRAIN_TIMEOUT)
    ap.add_argument("--lobby-timeout", type=positive("--lobby-timeout"),
                    default=LOBBY_TIMEOUT)
    ap.add_argument("--lobby-max-threads", type=positive("--lobby-max-threads"),
                    default=MAX_LOBBY_THREADS)
    ap.add_argument("--log-level", default="INFO",
                    choices=["DEBUG", "INFO", "WARNING", "ERROR"])
    ap.add_argument("--admin-token", default=os.environ.get("IMD_ADMIN_TOKEN", ""))
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
    MAX_CLIENTS = args.max_clients
    MAX_CLIENTS_PER_IP = args.max_clients_per_ip
    SEND_DRAIN_TIMEOUT = args.send_timeout
    LOBBY_TIMEOUT = args.lobby_timeout
    MAX_LOBBY_THREADS = args.lobby_max_threads
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
