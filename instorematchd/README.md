# instorematchd —— 自研联机服务端（大厅 + 中继）

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

异常流程也做了兜底（正常流程走不到，但玩家天天会碰到）：

- 房主没等人就开打 / 退出 → 挂着"加入"的人会被立刻通知失败，不会卡在连接中
- 点进一个房主已经不在线的房间、或者对端一直不接受 → 发起方收到 `CTL_TCP_CLOSE`
- 挂起建流有超时回收，反复点"加入"不会被踢下线
- `/recruit/start` 校验 `Keychip` 与房间伪 IP 一致，另有开房限速与房间总数上限
- **房主不在中继上的房间不公开**：房间是靠 HTTP 挂着续命的，如果房主的中继连接断了而
  进程还在，那间房会一直挂在列表里却谁也进不去（点进去只会得到"目标不在线"）。
  现在这种房间会被直接撤掉；客户端每 10 秒重报一次，房主重连后很快就会回来。
- 每条拒绝用的 `CTL_TCP_CLOSE` 都会在 `data` 里带上原因（`目标不在线` / `服务端挂起已满，稍后再试` …），
  客户端原样打进日志，排查时不用再上服务器翻 journal

这些逐条验证在 [`tests/py/test_edge.py`](../tests/py/test_edge.py)。

## 部署（推荐：一键脚本，systemd）

需要 Python **3.10+**（代码里用了 `str | None` 这类语法），不需要 pip / 数据库。

```bash
# 把 <你的域名或公网IP> 换成客户端能访问到的地址
sudo bash install.sh <你的域名或公网IP>

# 也可以指定端口：sudo bash install.sh maimai.example.com 20100 20101
```

脚本会：建 `instorematchd` 服务账号 → 装到 `/opt/instorematchd` →
用 `instorematchd.service.template` 生成 systemd 单元并重启服务 → 跑一遍协议自测。
（重跑即升级；脚本会先看端口有没有被**别的**服务占着，是自己在占就按升级处理。）

装完记得在**防火墙 / 云厂商安全组**放行 `20100/tcp`（大厅）和 `20101/tcp`（中继）。

```bash
curl -s http://127.0.0.1:20100/online      # {"totalUsers":0,"activeRecruits":0}
```

浏览器打开 `http://<你的服务器>:20100/` 就是看板。

## 看板与管理员视图

大厅端口必须对玩家开放，所以看板跟着一起公开。默认就已经**脱敏**：

| 谁 | 地址 | 能看到什么 |
| --- | --- | --- |
| 所有人 | `/`（看板）、`/api/status` | keychip 显示成 `W9999***877`、IP 显示成 `203.0.x.x`；**玩家名保留**；房间/曲目/难度正常 |
| 所有人 | `/info`、`/online`、`/recruit/list` | 这三个是**客户端要用的协议接口**，按协议原样返回。`/recruit/list` 给的是房主上传的招募数据（已去掉 `Keychip`），里面有玩家名和**伪 IP**（keychip 的 md5 前 4 字节，推不回原值）；想连这些都不暴露，就自己搭一台大厅 |
| 管理员 | `/admin?token=你的token`（页面）、`/api/status?token=…`、`/debug?token=…` | 未打码的完整信息（真 keychip、真公网 IP、原始事件） |

token 不在代码里，装的时候用环境变量给（会写进 `/etc/instorematchd.env`，权限 600）：

```bash
sudo IMD_ADMIN_TOKEN='你的token' bash install.sh <你的域名或公网IP>
```

不给这个变量也能正常跑：`/admin` 直接 403，公开看板照旧脱敏。
只想临时用一下也可以手改 `/etc/instorematchd.env` 后 `sudo systemctl restart instorematchd`。

想**关掉**管理员视图：`sudo rm /etc/instorematchd.env && sudo systemctl restart instorematchd`。

> 注意 token 是拼在 URL 里的、而且大厅是明文 HTTP：别把带 token 的链接发出去。
> 只想自己看的话，更稳的做法是走 SSH 隧道
> （`ssh -L 20100:127.0.0.1:20100 <服务器>`，然后本地开 `http://127.0.0.1:20100/admin?token=…`）。

## 部署（备选：Docker）

```bash
cd instorematchd
HOST_OVERRIDE=<你的域名或公网IP> docker compose up -d --build
docker compose logs -f
```

要开管理员视图就再加一个变量（不设则 `/admin` 403）：

```bash
HOST_OVERRIDE=<你的域名或公网IP> IMD_ADMIN_TOKEN=<你的token> docker compose up -d --build
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
| `--pending-timeout` | `10` | "房客点了加入、房主一直没接流"的挂起请求多久回收（秒）。到点会给两边各发一条 `CTL_TCP_CLOSE`，让房客的连接立刻失败，而不是一直转圈 |
| `--max-rooms` | `200` | 大厅同时在册的房间上限 |
| `--log-level` | `INFO` | `DEBUG` 会打印每一条协议消息，排查时很有用 |
| `--admin-token` | 空 | 管理员视图（`/admin`、完整版 `/api/status`、`/debug`）的 token。也可以用环境变量 `IMD_ADMIN_TOKEN`；不设就不开放 |

## 升级

```bash
git pull
sudo bash instorematchd/install.sh <你的域名或公网IP>   # 幂等，重跑即可
```

> 服务正在跑着也没关系：脚本会 `systemctl restart`，用上新拷贝的代码。
> 如果当前跑的是"吞 SIGTERM"那种旧代码，重启时会等 `TimeoutStopSec`（10 秒）才被杀掉，
> 看起来像卡住 —— 属正常。

## 卸载

```bash
sudo systemctl disable --now instorematchd
sudo rm /etc/systemd/system/instorematchd.service
sudo rm -rf /opt/instorematchd
sudo userdel instorematchd
```

## 说明

服务端只负责**大厅列表 + 中继转发**，它不参与游戏画面的任何事。
客户端那边要装两个东西：[`client/InStoreLink.dll`](../client/InStoreLink.dll)（联机本体，
源码在 [`tools/instorelink/`](../tools/instorelink/)）负责把游戏本体的局域网 party 接到这条
隧道上；[`client/InStoreMatch.dll`](../client/InStoreMatch.dll)（源码在
[`tools/InStoreMatch.cs`](../tools/InStoreMatch.cs)）负责把选曲界面底部的
「店内マッチング」那一格画出来。

排查时它可以帮你**看清数据流**：打开看板，如果对方开房时「当前房间」里出现了记录、
「在线玩家」里两个人都亮着，就说明服务端这边一切正常。
（别人看公开看板看到的是打码后的 keychip / IP；你自己带 token 看 `/admin` 才是全量。）

**人数上限不在服务端**：大厅和中继对人数没有限制（任意两个客户端之间都能建流）。
实际最多只能两个人一起玩，是因为客户端 mod（`InStoreLink`，上游的 `WorldLink` 也一样）是
「ふたり」实现，只认一个对端 —— 详见仓库根目录 README 的「已知问题」。
