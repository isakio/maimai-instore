# instorematchd —— 自己写的 NyanLink 服务端

参照 [MuNET-OSS/NyanLink](https://github.com/MuNET-OSS/NyanLink)（Kotlin 版 worldlinkd）重写，
**协议完全兼容，客户端 mod 不用改任何东西**，只要 `LobbyUrl` 指向本服务即可。

> 已经有跑着的大厅（`http://isakio.cn:20100`），**只想玩的话不需要自己搭** ——
> 直接看仓库 [README 的「方式 A」](../README.md)。这份文档是给想自建的人看的。

| | 原版 NyanLink（Docker 镜像） | instorematchd |
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

## 部署（推荐：一键脚本，systemd）

需要 Python **3.10+**（代码里用了 `str | None` 这类语法），不需要 pip / 数据库。

```bash
# 把 <你的域名或公网IP> 换成客户端能访问到的地址
sudo bash install.sh <你的域名或公网IP>

# 也可以指定端口：sudo bash install.sh maimai.example.com 20100 20101
```

脚本做四件事：建 `instorematchd` 服务账号 → 装到 `/opt/instorematchd` →
用 `instorematchd.service.template` 生成 systemd 单元并启动 → 跑一遍协议自测。

装完记得在**防火墙 / 云厂商安全组**放行 `20100/tcp`（大厅）和 `20101/tcp`（中继）。

```bash
curl -s http://127.0.0.1:20100/online      # {"totalUsers":0,"activeRecruits":0}
```

浏览器打开 `http://<你的服务器>:20100/` 就是看板。

## 部署（备选：Docker）

```bash
cd instorematchd
HOST_OVERRIDE=<你的域名或公网IP> docker compose up -d --build
docker compose logs -f
```

不想用容器编排，前台直接跑也行：

```bash
python3 instorematchd.py --host-override <你的域名或公网IP>
```

## 常用命令

```bash
systemctl status instorematchd              # 状态
sudo journalctl -u instorematchd -f         # 实时日志（服务跑在系统用户下，要 sudo 才看得到）
sudo journalctl -u instorematchd --since "10 min ago" | grep -E '\[(注册|断开|开房|关房)\]'
sudo systemctl restart instorematchd        # 重启（改完参数后，现在会秒退秒起）
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

## 升级

```bash
git pull
sudo bash instorematchd/install.sh <你的域名或公网IP>   # 幂等，重跑即可
```

## 卸载

```bash
sudo systemctl disable --now instorematchd
sudo rm /etc/systemd/system/instorematchd.service
sudo rm -rf /opt/instorematchd
sudo userdel instorematchd
```

## 说明

服务端只负责**大厅列表 + 中继转发**，它不参与游戏画面的任何事。
「选曲界面不显示店内マッチング分类」那个问题在客户端侧，由本仓库的
[`tools/InStoreMatch.cs`](../tools/InStoreMatch.cs)（编译产物 `client/InStoreMatch.dll`）解决。

排查时它可以帮你**看清数据流**：打开看板，如果对方开房时「当前房间」里出现了记录、
「在线玩家」里两个人都亮着，就说明服务端这边一切正常，问题在客户端。
