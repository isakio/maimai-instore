#!/usr/bin/env python3
"""文档一致性检查：把"文档里写的"和"仓库里的实物"对一遍。

为什么单列一步：字节数 / md5 / 补丁条数这些东西散在 README 和几份文档里，
改代码时最容易忘同步 —— 真踩过一次：`client/InStoreLink.dll` 重编了，
而文档里的 md5 还是旧的，照着文档校验的人会以为文件拿错了。
这里全部拿实物对照，跑一次几毫秒。

检查项：
  1. 发行 dll 的字节数 / md5 和文档里写的是否一致
  2. 所有 Markdown 里的相对链接是否都存在
  3. 补丁条数（从源码数出来的）和文档里写的是否一致
  4. 旧名字残留（WLDiag / nyanlinkd / install-instorematch / NYD_ 之类）
  5. third_party/ 里不该有二进制（README 承诺"不再分发第三方二进制"）
  6. 文档让人拿"33 行 ✓"做验收 —— 那 33 行就必须真的在默认配置下打出来

注意：`docs/技术笔记.md` 是**历史排错记录**，里面刻意保留着当时的旧名字/旧 md5，
所以对它只检查链接，不做其余四项。
"""

import hashlib
import os
import re
import sys

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))

PASS = []
FAIL = []


def check(ok, what, extra=""):
    (PASS if ok else FAIL).append(what)
    print("  %s %s%s" % ("✓" if ok else "✗", what, "" if ok else "  " + extra))


def read(path):
    with open(path, encoding="utf-8") as fh:
        return fh.read()


def md_files():
    out = []
    for root, dirs, files in os.walk(ROOT):
        dirs[:] = [d for d in dirs if d not in (".git", "__pycache__", "build")]
        for f in sorted(files):
            if f.endswith(".md"):
                out.append(os.path.join(root, f))
    return out


HISTORY = os.path.join(ROOT, "docs", "技术笔记.md")


def main():
    print("== 文档一致性检查 ==")

    # ---------------------------------------------------------- 1. 发行 dll
    print("1) 发行 dll 的字节数 / md5")
    dlls = {}
    for name in ("InStoreLink.dll", "InStoreMatch.dll", "MaimaiSteam.exe"):
        path = os.path.join(ROOT, "client", name)
        if not os.path.isfile(path):
            check(False, "client/%s 存在" % name)
            continue
        blob = open(path, "rb").read()
        dlls[name] = (len(blob), hashlib.md5(blob).hexdigest())
        check(True, "client/%s：%d 字节 / %s" % (name, len(blob), dlls[name][1]))

    hex_re = re.compile(r"\b[0-9a-fA-F]{32}\b")
    size_re = re.compile(r"(?:必须输出\s*)?(\d{5,7})\s*字节|必须输出\s*(\d{5,7})")
    bad = []
    for path in md_files():
        if path == HISTORY:
            continue
        rel = os.path.relpath(path, ROOT)
        for no, line in enumerate(read(path).splitlines(), 1):
            for name, (size, digest) in dlls.items():
                if name not in line and not (name == "InStoreLink.dll" and "$dll" in line):
                    continue
                for tok in hex_re.findall(line):
                    if tok.lower() != digest:
                        bad.append("%s:%d 写着 md5 %s，实际是 %s" % (rel, no, tok, digest))
                for m in size_re.finditer(line):
                    got = int(m.group(1) or m.group(2))
                    if got != size:
                        bad.append("%s:%d 写着 %d 字节，实际是 %d" % (rel, no, got, size))
    check(not bad, "文档里的字节数 / md5 和 client/ 里那两份一致",
          "\n      ".join(bad))

    # ---------------------------------------------------------- 2. 相对链接
    print("2) Markdown 相对链接")
    missing = []
    total = 0
    for path in md_files():
        rel = os.path.relpath(path, ROOT)
        for m in re.finditer(r"\[[^\]]*\]\(([^)]+)\)", read(path)):
            target = m.group(1).strip()
            if target.startswith(("http://", "https://", "#", "mailto:")):
                continue
            target = target.split("#")[0]
            if not target:
                continue
            total += 1
            full = os.path.normpath(os.path.join(os.path.dirname(path), target))
            if not os.path.exists(full):
                missing.append("%s -> %s" % (rel, target))
    check(not missing, "%d 条相对链接全部有效" % total, "\n      ".join(missing))

    # ---------------------------------------------------------- 3. 补丁条数
    print("3) 补丁条数")
    src_dir = os.path.join(ROOT, "tools", "instorelink")
    counts = {}
    for f in sorted(os.listdir(src_dir)):
        if f.startswith("Patches") and f.endswith(".cs"):
            counts[f] = read(os.path.join(src_dir, f)).count("[HarmonyPatch(")
    net = counts.get("PatchesNet.cs", 0)
    party = counts.get("PatchesParty.cs", 0)
    total_patches = net + party
    check(total_patches > 0, "源码里共 %d 条补丁（PatchesNet %d + PatchesParty %d）"
          % (total_patches, net, party))

    wants = [
        ("tools/README.md", "%d 个补丁" % net),
        ("tools/README.md", "%d 个补丁" % party),
        ("README.md", "共 %d 条生效" % total_patches),
        ("docs/给朋友看-安装步骤.md", "共 %d 条生效" % total_patches),
        ("docs/双人联机配置清单.md", "共 %d 条生效" % total_patches),
        ("docs/客户端mod实现.md", "%d 条补丁" % total_patches),
        # 「N 行 ✓」这个说法出现在这几份"照着做"的文档里，行数必须等于补丁条数
        ("README.md", "%d 行" % total_patches),
        ("docs/给朋友看-安装步骤.md", "%d 行" % total_patches),
        ("docs/双人联机配置清单.md", "%d 行" % total_patches),
    ]
    bad = []
    for rel, needle in wants:
        path = os.path.join(ROOT, rel)
        if not os.path.isfile(path) or needle not in read(path):
            bad.append("%s 里找不到「%s」" % (rel, needle))
    check(not bad, "文档里的补丁条数和源码一致", "\n      ".join(bad))

    # ---------------------------------------------------------- 4. 旧名字
    print("4) 旧名字残留")
    stale = ("WLDiag", "nyanlinkd", "install-instorematch", "NyanLink-Companion", "NYD_")
    exts = (".py", ".cs", ".sh", ".ps1", ".md", ".toml", ".template", ".yml", ".yaml",
            ".json", ".txt")
    hits = []
    self_path = os.path.abspath(__file__)      # 本文件自己写着这些词，别查自己
    for root, dirs, files in os.walk(ROOT):
        dirs[:] = [d for d in dirs if d not in (".git", "__pycache__", "build")]
        for f in sorted(files):
            path = os.path.join(root, f)
            if os.path.abspath(path) == self_path or path == HISTORY or not f.endswith(exts):
                continue
            try:
                text = read(path)
            except (UnicodeDecodeError, OSError):
                continue
            for no, line in enumerate(text.splitlines(), 1):
                for word in stale:
                    if word in line:
                        hits.append("%s:%d %s" % (os.path.relpath(path, ROOT), no, word))
    check(not hits, "没有旧名字残留（技术笔记除外）", "\n      ".join(hits))

    # ---------------------------------------------------------- 5. 第三方二进制
    print("5) third_party/ 里的二进制")
    found = []
    tp = os.path.join(ROOT, "third_party")
    for root, dirs, files in os.walk(tp):
        for f in files:
            if f.endswith((".dll", ".exe", ".jar", ".zip", ".tar", ".gz")):
                found.append(os.path.relpath(os.path.join(root, f), ROOT))
    check(not found, "third_party/ 里只有文档（没有第三方二进制）", "\n      ".join(found))

    # ---------------------------------------------------- 6. 验收日志真的会打出来吗
    # 踩过：这几份文档都写着"日志里会有 33 行 ✓ 补丁名 → 目标方法"，
    # 而源码里那行是 LinkLog.Info（只在 Debug=true 时打）——
    # 默认配置的玩家照着文档去找，一条都找不到，会以为插件没加载。
    print("6) 验收日志：逐条 ✓ 是否常显")
    patching = read(os.path.join(ROOT, "tools", "instorelink", "LinkPatching.cs"))
    always = re.search(r'LinkLog\.Msg\(\s*"\s*✓', patching) is not None
    debug_only = re.search(r'LinkLog\.Info\(\s*"\s*✓', patching) is not None
    check(always and not debug_only,
          "InStoreLink 的逐条 ✓ 用 LinkLog.Msg（不受 Debug 开关影响）",
          "LinkPatching.cs 里那条变成 LinkLog.Info 了：默认 Debug=false 时一行都不会打，"
          "而文档还在让人拿这 %d 行做验收" % total_patches)

    match = read(os.path.join(ROOT, "tools", "InStoreMatch.cs"))
    check("MelonLogger.Msg(\"[InStoreMatch] 挂钩成功" in match,
          "InStoreMatch 的挂钩成功行也是常显（MelonLogger.Msg）",
          "InStoreMatch.cs 里的挂钩成功行被改成有条件打印了")

    # ---------------------------------------------- 7. 其它被文档点名的日志行
    # 同一类坑的第二例：「已连接中继 ……」被 README / 配置清单 / 客户端实现文档 /
    # 给朋友看的说明四处拿来当"装好了"的验收依据，而那行原来也是 Debug-only。
    print("7) 验收日志：其它被文档点名的行是否常显")
    wanted = [
        ("LinkClient.cs", "已连接中继"),
    ]
    bad = []
    for fname, keyword in wanted:
        text = read(os.path.join(ROOT, "tools", "instorelink", fname))
        line = None
        for l in text.splitlines():
            if keyword in l and "LinkLog." in l:
                line = l.strip()
                break
        if line is None:
            bad.append("%s 里找不到打印「%s」的语句" % (fname, keyword))
        elif not line.startswith("LinkLog.Msg("):
            bad.append("%s 里的「%s」用的是 %s（默认 Debug=false 不会打，"
                       "而文档让人拿它做验收）" % (fname, keyword, line.split("(")[0]))
    check(not bad, "被文档点名的验收日志行都是常显", "\n      ".join(bad))

    print()
    if FAIL:
        print("失败 %d 项：" % len(FAIL))
        for f in FAIL:
            print("  -", f)
        return 1
    print("全部通过（%d 项）" % len(PASS))
    return 0


if __name__ == "__main__":
    sys.exit(main())
