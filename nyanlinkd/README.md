# nyanlinkd —— 自己写的 NyanLink 服务端

参照 [MuNET-OSS/NyanLink](https://github.com/MuNET-OSS/NyanLink)（Kotlin 版 worldlinkd）重写，
**协议完全兼容，客户端 mod 不用改任何东西**，只要 `LobbyUrl` 指向本服务即可。

| | 原版 NyanLink（Docker 镜像） | nyanlinkd |
| --- | --- | --- |
| 语言 / 依赖 | Kotlin + JVM | Python 3.12，**只用标准库** |
| 镜像体积 | ~200 MB | 0（原生运行） |
| `CTL_TCP_CLOSE` | 忽略（流表泄漏） | **正确处理** |
| 房间 TTL / 心跳超时 | 硬编码 | 可配 |
| 可观测性 | 只有滚动日志 | **网页看板** + 结构化事件日志 |
| 容错 | 单条异常可能断连 | 逐条 try/except，乱码不影响 |

## 功能

- **大厅（HTTP :20100）**：`/recruit/start`、`/recruit/finish`、`/recruit/list`、`/info`、`/online`
- **中继（TCP :20101）**：注册、心跳、建流、接流、关流、发数据、广播
- **网页看板（`http://<服务器>:20100/`）**：实时在线玩家、房间列表、事件流水、累计统计
- **`/api/status`**：看板用的 JSON，排查问题时直接 curl 它

## 部署（推荐：systemd 原生）

服务器上已有 Python 3.12，不需要装任何东西。

```bash
# 1. 停掉旧的 NyanLink 容器，释放 20100 / 20101
cd /opt/nyanlink && docker compose down

# 2. 建目录（在你的电脑上执行）
ssh myserver 'sudo mkdir -p /opt/nyanlinkd && sudo chown isakio:isakio /opt/nyanlinkd'
scp /home/isakio/myserver/maimai/nyanlinkd/nyanlinkd.py \
    /home/isakio/myserver/maimai/nyanlinkd/nyanlinkd.service \
    /home/isakio/myserver/maimai/nyanlinkd/test_protocol.py \
    myserver:/opt/nyanlinkd/

# 3. 装并启动（在服务器上执行）
sudo cp /opt/nyanlinkd/nyanlinkd.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now nyanlinkd
systemctl status nyanlinkd --no-pager

# 4. 验证
curl -s http://127.0.0.1:20100/online
python3 /opt/nyanlinkd/test_protocol.py      # 跑一遍协议自测，应全部通过
```

然后浏览器打开 `http://isakio.cn:20100/` 就是看板。

## 部署（备选：Docker）

不想动 systemd 就用这个，但要从 Docker Hub 拉 `python:3.12-alpine`（国内可能慢）。

```bash
cd /opt/nyanlink && docker compose down
scp /home/isakio/myserver/maimai/nyanlinkd/{nyanlinkd.py,docker-compose.yml} myserver:/opt/nyanlinkd/
ssh myserver 'cd /opt/nyanlinkd && docker compose up -d && docker compose logs -f'
```

## 常用命令

```bash
systemctl status nyanlinkd          # 状态
journalctl -u nyanlinkd -f          # 实时日志
journalctl -u nyanlinkd --since "10 min ago" | grep -E '\[(注册|断开|开房|关房)\]'
sudo systemctl restart nyanlinkd    # 重启（改完参数后）
```

## 参数

| 参数 | 默认 | 说明 |
| --- | --- | --- |
| `--bind` | `0.0.0.0` | 监听地址 |
| `--lobby-port` | `20100` | 大厅 HTTP 端口 |
| `--relay-port` | `20101` | 中继 TCP 端口 |
| `--host-override` | 空 | `/info` 返回的中继主机名。**走反代或域名访问时必须填**，否则客户端会拿到 127.0.0.1 |
| `--recruit-ttl` | `30` | 房间多久没刷新就消失（秒） |
| `--heartbeat-timeout` | `30` | 多久没心跳就断开（秒） |
| `--log-level` | `INFO` | `DEBUG` 会打印每一条协议消息，排查时很有用 |

## 改完代码后

```bash
scp nyanlinkd.py myserver:/opt/nyanlinkd/ && ssh myserver 'sudo systemctl restart nyanlinkd'
```

## 回滚到原版

```bash
sudo systemctl disable --now nyanlinkd
cd /opt/nyanlink && docker compose up -d
```

## 说明

**这个服务端解决不了「选曲界面不显示店内マッチング分类」那个问题**——那是客户端 mod
（`WorldLink.dll` 对 Unity 游戏的 Harmony 注入）的事，服务端只负责转发。

但它能让你**看清数据流**：打开看板，如果对方开房时「当前房间」里出现了记录、
「在线玩家」里两个人都亮着，就说明服务端这边一切正常，问题 100% 在客户端。
