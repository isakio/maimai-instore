#!/usr/bin/env python3
"""
假玩家 —— 在大厅/中继上模拟一个正在招募的玩家，用来单独测试客户端。

它会做四件事：
  1. 用 --keychip 注册到中继（和真客户端走同一套协议）
  2. 往大厅 POST 一条招募记录，并每 --interval 秒刷新一次（防止过期）
  3. 持续回心跳
  4. --auto-accept 时，自动接受对方的建流请求、并把收到的数据原样回传

关于"按 NEXT"：
  进房之后的"准备/开始"是**游戏自己的协议**（mod 只是把包原样转发，并把加密钩掉，
  所以包体是明文再 base64）。假玩家目前只会把收到的包回弹，游戏看到包里的
  机器标识还是自己，多半不认，于是卡在准备页。
  默认会把收到的每个包（含广播）打到日志里；加 --rewrite-ip 可以试着在回包时
  把包里的对端伪 IP 换成本机的，让游戏当成"另一台机器"。

用法：
  python3 fake_player.py                      # 连 127.0.0.1:20100/20101
  python3 fake_player.py --server maimai.example.com   # 连远程
  python3 fake_player.py --name 假朋友 --music-id 12054 --auto-accept

注意：假玩家自己的 keychip 要和真客户端不同，否则会把对方踢下线。
"""

import argparse
import base64
import hashlib
import json
import socket
import struct
import threading
import time
import urllib.request


def keychip_to_stub(keychip: str) -> int:
    d = hashlib.md5(keychip.encode()).digest()
    return (d[0] << 24) | (d[1] << 16) | (d[2] << 8) | d[3]


class FakePlayer:
    def __init__(self, args):
        self.args = args
        self.stub = keychip_to_stub(args.keychip)
        self.sock = None
        self.running = True
        self.streams = {}          # sid -> 对端 stub
        self.lobby = f"http://{args.server}:{args.lobby_port}"

    # ------------------------------------------------------------ 中继
    def connect_relay(self):
        while self.running:
            try:
                s = socket.create_connection((self.args.server, self.args.relay_port), timeout=5)
                s.settimeout(None)
                self.sock = s
                self.send(f"1,1,,,,,,,,,,,,,,,{self.args.keychip}")
                print(f"[relay] 已连接 {self.args.server}:{self.args.relay_port}，"
                      f"注册 {self.args.keychip}（伪IP {self.stub}）", flush=True)
                threading.Thread(target=self.heartbeat_loop, daemon=True).start()
                self.read_loop()
            except Exception as exc:
                print(f"[relay] 连接失败：{exc}，3 秒后重试", flush=True)
            self.running and time.sleep(3)

    def send(self, line: str):
        if self.sock:
            try:
                self.sock.sendall((line + "\n").encode())
            except Exception:
                pass

    def heartbeat_loop(self):
        while self.running:
            self.send("1,3")
            time.sleep(1)

    def read_loop(self):
        buf = b""
        while self.running:
            try:
                chunk = self.sock.recv(4096)
            except Exception:
                break
            if not chunk:
                break
            buf += chunk
            while b"\n" in buf:
                line, buf = buf.split(b"\n", 1)
                self.on_message(line.decode("utf-8", "replace").strip())

    def on_message(self, line: str):
        if not line:
            return
        f = line.split(",")
        try:
            cmd = int(f[1]) if len(f) > 1 and f[1] else None
        except ValueError:
            return
        if cmd == 3 or cmd is None:
            return
        sid = int(f[3]) if len(f) > 3 and f[3] else None
        src = f[4] if len(f) > 4 and f[4] else None
        sport = f[5] if len(f) > 5 and f[5] else ""
        dst = f[6] if len(f) > 6 and f[6] else None
        dport = f[7] if len(f) > 7 and f[7] else ""
        data = ",".join(f[16:]) if len(f) > 16 else ""
        names = {4: "建流请求", 5: "接流确认", 7: "关流", 21: "数据", 22: "广播", 1: "注册"}
        if self.args.dump:
            print(f"[relay] << {names.get(cmd, cmd):<6} sid={sid} src={src} dst={dst}"
                  f" data[{len(data)}]={data[:600]}", flush=True)
        else:
            print(f"[relay] 收到 {names.get(cmd, cmd)} sid={sid} src={src} dst={dst}"
                  f" data={data[:40]}", flush=True)

        if not self.args.auto_accept:
            return
        if cmd == 4:                      # 对方要建房 → 接
            self.streams[sid] = src
            self.send(f"1,5,6,{sid},{self.stub},{sport},{src},{sport}")
            print(f"[relay] 已接受流 {sid}", flush=True)
        elif cmd == 22:                   # 广播 → 也回一个，让游戏觉得"对端在广播"
            self.echo(22, sid, src, sport, dst, dport, data)
        elif cmd == 21:                   # 数据 → 回传（可选改写伪 IP）
            self.echo(21, sid, src, sport, dst, dport, data)

    # 把收到的包按原样（或改写伪 IP 后）发回给对端
    def echo(self, cmd, sid, src, sport, dst, dport, data):
        payload = data
        if self.args.rewrite_ip and src:
            payload = self.rewrite_ip(payload, int(src))
        arr = ["1", str(cmd), "6", "" if sid is None else str(sid),
               str(self.stub), sport, "" if src is None else src, sport,
               "", "", "", "", "", "", "", "", payload]
        self.send(",".join(arr))

    # 游戏包体是明文（mod 把加密钩掉了）再 base64 塞在中继消息里，
    # 里面会带上"发送方的伪 IP"。回包时把它换成本机的，游戏才会当成另一台机器。
    def rewrite_ip(self, payload, peer_stub):
        if not payload:
            return payload
        try:
            raw = base64.b64decode(payload + "=" * (-len(payload) % 4))
        except Exception:
            return payload
        if not raw:
            return payload
        hit = 0
        for order in ("<", ">"):
            try:
                pat = struct.pack(order + "I", peer_stub & 0xFFFFFFFF)
                mine = struct.pack(order + "I", self.stub & 0xFFFFFFFF)
            except Exception:
                continue
            if pat in raw:
                hit += raw.count(pat)
                raw = raw.replace(pat, mine)
        if not hit:
            return payload
        print(f"[relay] 改写 {hit} 处伪IP {peer_stub} -> {self.stub}", flush=True)
        return base64.b64encode(raw).decode()

    # ------------------------------------------------------------ 大厅
    def post(self, path, obj):
        req = urllib.request.Request(
            self.lobby + path, method="POST",
            data=json.dumps(obj, ensure_ascii=False).encode(),
            headers={"Content-Type": "application/json"})
        return urllib.request.urlopen(req, timeout=5).read().decode()

    def recruit_body(self):
        # 字段值尽量抄自真实招募（图标 ID、UserID 这些都是游戏里真实存在的，
        # 乱填有概率让客户端的招募界面崩掉）
        return {
            "Keychip": self.args.keychip,
            "RecruitInfo": {
                "MechaInfo": {
                    "IsJoin": True,
                    "IpAddress": self.stub,
                    "MusicID": self.args.music_id,
                    "Entrys": [True, False],
                    "UserIDs": [1240811538, 281474976710657],
                    "UserNames": [self.args.name, "ＧＵＥＳＴ"],
                    "IconIDs": [700101, 1],
                    "FumenDifs": [self.args.difficulty, -1],
                    "Rateing": [0, 0], "ClassValue": [0, 0],
                    "MaxClassValue": [0, 0], "UserType": [3, 0],
                },
                "MusicID": self.args.music_id,
                "GroupID": 0, "EventModeID": False, "JoinNumber": 0,
                "PartyStance": 0,
                "_startTimeTicks": 639266000000000000,
                "_recvTimeTicks": 0,
            },
        }

    def recruit_loop(self):
        if self.args.delay > 0:
            print(f"[lobby] 先等 {self.args.delay} 秒，让游戏登录并加载曲库…", flush=True)
            time.sleep(self.args.delay)
        while self.running:
            try:
                self.post("/recruit/start", self.recruit_body())
                print(f"[lobby] 招募已刷新（{self.args.name} / 曲目 {self.args.music_id}）", flush=True)
            except Exception as exc:
                print(f"[lobby] 刷新招募失败：{exc}", flush=True)
            time.sleep(self.args.interval)

    def run(self):
        print(f"[main] 假玩家启动：{self.args.name}，曲目 {self.args.music_id}，"
              f"难度 {self.args.difficulty}，自动接流={'开' if self.args.auto_accept else '关'}",
              flush=True)
        threading.Thread(target=self.recruit_loop, daemon=True).start()
        try:
            self.connect_relay()
        except KeyboardInterrupt:
            pass
        finally:
            self.running = False
            try:
                self.post("/recruit/finish", self.recruit_body())
                print("[lobby] 已撤销招募", flush=True)
            except Exception:
                pass


def main():
    ap = argparse.ArgumentParser(description="模拟一个正在招募的玩家")
    ap.add_argument("--server", default="127.0.0.1")
    ap.add_argument("--lobby-port", type=int, default=20100)
    ap.add_argument("--relay-port", type=int, default=20101)
    ap.add_argument("--keychip", default="W8888888888")
    ap.add_argument("--name", default="假朋友")
    ap.add_argument("--music-id", type=int, default=12054)
    ap.add_argument("--difficulty", type=int, default=4, help="0=BASIC … 4=MASTER")
    ap.add_argument("--interval", type=int, default=15, help="多久刷新一次招募（秒）")
    ap.add_argument("--delay", type=int, default=0,
                    help="启动后先等多少秒再开始招募（游戏要先登录、加载完曲库，"
                         "否则客户端会报 'music xxx is not available'）")
    ap.add_argument("--auto-accept", action="store_true", help="自动接受对方的联机请求")
    ap.add_argument("--no-dump", dest="dump", action="store_false",
                    help="不打印收到的每个包（默认打印，用来观察准备页的握手）")
    ap.set_defaults(dump=True)
    ap.add_argument("--rewrite-ip", action="store_true",
                    help="回包时把对端的伪 IP 换成本机的（实验：让游戏认它是另一台机器）")
    FakePlayer(ap.parse_args()).run()


if __name__ == "__main__":
    main()
