# 上游：NyanLink（协议与最初实现）

本仓库**不再分发** NyanLink 的任何二进制了：客户端联机部分由本仓库自己的
[`InStoreLink`](../../tools/instorelink/) 实现（协议逐字节兼容），服务端由
[`instorematchd`](../../instorematchd/) 实现。这里放着它的许可与出处，
因为我们的这两份都是**照着它逆向重写**的衍生作品。

| | |
| --- | --- |
| 来源 | <https://github.com/MuNET-OSS/NyanLink>（上游 <https://github.com/MewoLab/worldlinkd>） |
| 我们参考它的什么 | 线协议（消息格式、命令号、伪 IP 算法）、游戏侧的关键机制（影子 socket、跳过联网自检、关掉包加解密、招募走 HTTP……） |
| 许可 | MIT（Copyright (c) 2025 Azalea），全文见同目录 [LICENSE](LICENSE) |

三点说明：

1. 我们**重写过**：`tools/instorelink/` 下的代码是自己写的，只是行为与它保持一致，
   所以两边一家用我们这份、一家用上游那份也能连上（协议一致）。
2. 上游的 `WorldLink.dll`（官方发布包 `NyanLink v2.0.0rev1` 里那份，
   50176 字节 / md5 `9dfa62d5cba41deac0c2c74334ef8371`）现在只在 `legacy-worldlink`
   分支上还留着，用来对照和回滚。
3. 完整的协议规格、上游源码导读、以及我们踩过的坑，见
   [`docs/客户端mod实现.md`](../../docs/客户端mod实现.md)。
