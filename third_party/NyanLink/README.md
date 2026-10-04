# 第三方组件：NyanLink（`client/WorldLink.dll`）

本仓库把 NyanLink 的客户端 mod 原样带了一份，目的是**只从本仓库就能装完**，
不用再去别的地方下载。

| | |
| --- | --- |
| 来源 | <https://github.com/MuNET-OSS/NyanLink>（上游 <https://github.com/MewoLab/worldlinkd>） |
| 出处 | 官方发布包 `NyanLink v2.0.0rev1` 里的 `WorldLink.dll` |
| 是否改动 | **未做任何修改**，原样分发 |
| 校验 | 50176 字节 / md5 `9dfa62d5cba41deac0c2c74334ef8371` |
| 许可 | MIT（Copyright (c) 2025 Azalea），全文见同目录 [LICENSE](LICENSE) |

两点说明：

1. 联机时**两台机器必须用同一个文件**（md5 一致），所以这里固定保留这一份。
2. 代码本身是 NyanLink 的，不是我们的；我们在本仓库里只做了一份配置模板
   （`WorldLink.toml`，由安装脚本生成）和一个独立的客户端插件
   （`client/InStoreMatch.dll`，源码在 `tools/InStoreMatch.cs`）。
