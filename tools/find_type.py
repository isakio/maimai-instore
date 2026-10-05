#!/usr/bin/env python3
"""在游戏本体的 Assembly-CSharp.dll 里按名字找类型（全名 / 命名空间 / 基类）。

写补丁时用来确认"这个类到底在哪个命名空间"，省得靠猜。

用法：
  PYTHONPATH=/tmp/dntools python3 tools/find_type.py Client Packet NFSocket IManager
  PYTHONPATH=/tmp/dntools python3 tools/find_type.py --members Client   # 顺便列出成员
"""

import os
import sys

import dnfile

DEFAULT = os.environ.get(
    "IL_ASSEMBLY",
    "/mnt/d/game/maimai/SDEZ1.70/Package/Sinmai_Data/Managed/Assembly-CSharp.dll")


def main(argv):
    show_members = "--members" in argv
    names = [a for a in argv if not a.startswith("--")]
    asm = os.environ.get("IL_ASSEMBLY", DEFAULT)
    pe = dnfile.dnPE(asm)
    want = [n.lower() for n in names]
    hits = 0
    for rid, t in enumerate(pe.net.mdtables.TypeDef.rows, start=1):
        name = str(t.TypeName)
        if want and name.lower() not in want:
            continue
        ns = str(t.TypeNamespace)
        full = (ns + "." if ns else "") + name
        base = ""
        try:
            if t.Extends and t.Extends.row is not None:
                base = " : " + str(t.Extends.row.TypeName)
        except Exception:
            pass
        print("TYPE %s%s   (rid=%s)" % (full, base, rid))
        hits += 1
        if not show_members:
            continue
        for m in t.MethodList:
            if m.row is None:
                continue
            params = []
            try:
                for p in (m.row.ParamList or []):
                    if p.row is not None:
                        params.append(str(p.row.Name))
            except Exception:
                pass
            print("    .%s(%s)" % (m.row.Name, ", ".join(params)))
        for f in t.FieldList:
            if f.row is None:
                continue
            print("    f %s" % (f.row.Name,))
    if not hits:
        print("没找到：" + ", ".join(names))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
