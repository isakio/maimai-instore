#!/usr/bin/env python3
"""
游戏程序集的 IL 分析小工具（反编译 maimai 的 Assembly-CSharp.dll 用）

依赖：
  python3 -m pip install --break-system-packages --target=/tmp/dntools dnfile dncil

用法：
  PYTHONPATH=/tmp/dntools python3 il.py list <关键词>        # 找方法，列出 rid
  PYTHONPATH=/tmp/dntools python3 il.py callers <方法名>     # 谁调用了它
  PYTHONPATH=/tmp/dntools python3 il.py dump <方法名> [起] [止]  # 反汇编（按 IL 偏移过滤）
  PYTHONPATH=/tmp/dntools python3 il.py type <rid>          # 方法属于哪个类
  PYTHONPATH=/tmp/dntools python3 il.py methods <类名关键词>  # 列出某个类的方法
  PYTHONPATH=/tmp/dntools python3 il.py fields <类名关键词>   # 列出某个类的字段
"""

import os
import sys

import dnfile
from dncil.cil.body import CilMethodBody
from dncil.cil.body.reader import CilMethodBodyReaderBase
from dncil.clr.token import Token, StringToken

ASSEMBLY = os.environ.get(
    "IL_ASSEMBLY",
    "/mnt/d/game/maimai/SDEZ1.70/Package/Sinmai_Data/Managed/"
    "Assembly-CSharp.dll")

pe = dnfile.dnPE(ASSEMBLY)
ROWS = pe.net.mdtables.MethodDef.rows

# rid -> 所属类的全名
_OWNER = {}
for _t in pe.net.mdtables.TypeDef.rows:
    _name = ((str(_t.TypeNamespace) + ".") if _t.TypeNamespace else "") + str(_t.TypeName)
    for _mi in _t.MethodList:
        _rid = _mi.row_index
        if _rid:
            _OWNER[_rid] = _name


class Reader(CilMethodBodyReaderBase):
    def __init__(self, pe, offset):
        self.pe, self.offset = pe, offset

    def read(self, n):
        data = self.pe.get_data(self.offset, n)
        self.offset += n
        return data

    def tell(self):
        return self.offset

    def seek(self, offset):
        self.offset = offset

    def read_at(self, offset, n):
        return self.pe.get_data(offset, n)


def resolve(tok):
    t, rid = tok.table, tok.rid
    try:
        if t == 0x70:
            return repr(str(pe.net.user_strings.get(rid)))
        if t == 0x06:
            return "M:" + str(ROWS[rid - 1].Name)
        if t == 0x04:
            return "F:" + str(pe.net.mdtables.Field.rows[rid - 1].Name)
        if t == 0x0A:
            return "MR:" + str(pe.net.mdtables.MemberRef.rows[rid - 1].Name)
        if t == 0x01:
            return "TR:" + str(pe.net.mdtables.TypeRef.rows[rid - 1].TypeName)
        if t == 0x02:
            return "TD:" + str(pe.net.mdtables.TypeDef.rows[rid - 1].TypeName)
    except Exception as exc:
        return f"tok?{exc}"
    return f"t{hex(t)}:{rid}"


def body_of(index):
    m = ROWS[index]
    if not m.Rva:
        raise ValueError(f'{m.Name} 没有方法体（abstract/extern）')
    return CilMethodBody(Reader(pe, pe.get_offset_from_rva(m.Rva)))


def cmd_list(keyword):
    for i, m in enumerate(ROWS):
        if keyword.lower() in str(m.Name).lower():
            print(f"  rid={i + 1:6d}  {m.Name}")


def cmd_type(rid):
    rid = int(str(rid).replace("rid:", ""))
    m = ROWS[rid - 1]
    print(f"  rid={rid}  {_OWNER.get(rid, '?')}.{m.Name}")


def cmd_methods(keyword):
    kw = keyword.lower()
    for t in pe.net.mdtables.TypeDef.rows:
        name = ((str(t.TypeNamespace) + ".") if t.TypeNamespace else "") + str(t.TypeName)
        if kw not in name.lower():
            continue
        print(f"=== {name} ===")
        for mi in t.MethodList:
            rid = mi.row_index
            if rid and 1 <= rid <= len(ROWS):
                print(f"  rid={rid:6d}  {ROWS[rid - 1].Name}")


def cmd_fields(keyword):
    kw = keyword.lower()
    frows = pe.net.mdtables.Field.rows
    for t in pe.net.mdtables.TypeDef.rows:
        name = ((str(t.TypeNamespace) + ".") if t.TypeNamespace else "") + str(t.TypeName)
        if kw not in name.lower():
            continue
        print(f"=== {name} ===")
        for fi in t.FieldList:
            rid = fi.row_index
            if rid and 1 <= rid <= len(frows):
                print(f"  rid={rid:6d}  {frows[rid - 1].Name}")


def cmd_callers(name):
    target = {i + 1 for i, m in enumerate(ROWS) if str(m.Name) == name}
    if not target:
        print("找不到方法:", name)
        return
    found = 0
    for i, m in enumerate(ROWS):
        if not m.Rva:
            continue
        try:
            body = body_of(i)
        except Exception:
            continue
        for ins in body.instructions:
            op = ins.operand
            if isinstance(op, Token) and op.table == 6 and op.rid in target:
                print(f"  {ROWS[i].Name} 调用 {name} @ IL {ins.offset}")
                found += 1
                break
    print(f"共 {found} 处调用")


def cmd_dump(name, lo=0, hi=1 << 30):
    if name.startswith("rid:"):          # 按 rid 精确定位（同名方法很多时用）
        rid = int(name[4:])
        m = ROWS[rid - 1]
        body = body_of(rid - 1)
        print(f"=== rid={rid} {m.Name} rva={m.Rva} 共 {len(body.instructions)} 条 ===")
        for ins in body.instructions:
            if not (lo <= ins.offset <= hi):
                continue
            op = ins.operand
            if isinstance(op, StringToken):
                extra = repr(str(op.value))
            elif isinstance(op, Token):
                extra = f"{op.table},{op.rid} {resolve(op)}"
            elif op is not None:
                extra = str(op)
            else:
                extra = ""
            print(f"  {ins.offset:7d} {str(ins.opcode):14s} {extra}")
        return
    for i, m in enumerate(ROWS):
        if str(m.Name) != name or not m.Rva:
            continue
        body = body_of(i)
        print(f"=== {name} rid={i + 1} rva={m.Rva} 共 {len(body.instructions)} 条 ===")
        for ins in body.instructions:
            if not (lo <= ins.offset <= hi):
                continue
            op = ins.operand
            if isinstance(op, StringToken):
                extra = repr(str(op.value))
            elif isinstance(op, Token):
                extra = f"{op.table},{op.rid} {resolve(op)}"
            elif op is not None:
                extra = str(op)
            else:
                extra = ""
            print(f"  {ins.offset:7d} {str(ins.opcode):14s} {extra}")
        return
    print("找不到方法:", name)


if __name__ == "__main__":
    if len(sys.argv) < 3:
        print(__doc__)
        sys.exit(1)
    mode, arg = sys.argv[1], sys.argv[2]
    if mode == "list":
        cmd_list(arg)
    elif mode == "callers":
        cmd_callers(arg)
    elif mode == "dump":
        lo = int(sys.argv[3]) if len(sys.argv) > 3 else 0
        hi = int(sys.argv[4]) if len(sys.argv) > 4 else 1 << 30
        cmd_dump(arg, lo, hi)
    elif mode == "type":
        cmd_type(arg)
    elif mode == "methods":
        cmd_methods(arg)
    elif mode == "fields":
        cmd_fields(arg)
    else:
        print(__doc__)
