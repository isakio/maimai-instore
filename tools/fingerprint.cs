// 程序集指纹：判断「仓库里发行的那份 dll」是不是真的由当前源码编出来的。
//
// 为什么不能用 md5 比：csc 的输出不可复现 —— 每次编译 MVID 和时间戳都不同，
// 同一份源码编两次 md5 都不一样（实测过），所以字节比对永远对不上。
//
// 这个指纹只取**与编译随机性无关**的东西：类型/字段/方法/成员引用/字符串字面量/
// IL 里的调用目标/特性，全部排序后求 sha256。同一份源码编出来的 dll 指纹必然相同；
// 源码改了而发行版忘了重编，指纹就对不上。
//
// 真踩过这个坑：client/InStoreLink.dll 收进仓库之后，源码又改了 LinkConfig（加 BOM 兼容），
// 发行版没跟着重编 —— 字节数一样、md5 看不出来，是靠这个指纹发现的。
//
//   csc /r:Mono.Cecil.dll /out:fingerprint.exe fingerprint.cs
//   fingerprint.exe <dll>            打印指纹
//   fingerprint.exe <dllA> <dllB>    比对；不同则列出差异并返回 1

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

public static class Fingerprint
{
    public static int Main(string[] argv)
    {
        try { Console.OutputEncoding = new UTF8Encoding(false); } catch (Exception) { }

        if (argv.Length < 1)
        {
            Console.WriteLine("用法：fingerprint.exe <dll> [另一个 dll]");
            return 2;
        }

        List<string> a;
        try
        {
            a = Lines(argv[0]);
        }
        catch (Exception ex)
        {
            Console.WriteLine("读不了 " + argv[0] + "：" + ex.Message);
            return 2;
        }
        if (argv.Length == 1)
        {
            Console.WriteLine("指纹 " + Digest(a) + "  " + argv[0] + "（" + a.Count + " 项）");
            return 0;
        }

        List<string> b;
        try
        {
            b = Lines(argv[1]);
        }
        catch (Exception ex)
        {
            Console.WriteLine("读不了 " + argv[1] + "：" + ex.Message);
            return 2;
        }
        string da = Digest(a), db = Digest(b);
        Console.WriteLine("A  " + da + "  " + argv[0]);
        Console.WriteLine("B  " + db + "  " + argv[1]);
        if (da == db)
        {
            Console.WriteLine("指纹一致");
            return 0;
        }

        HashSet<string> sa = new HashSet<string>(a), sb = new HashSet<string>(b);
        List<string> onlyA = a.Where(x => !sb.Contains(x)).Distinct().ToList();
        List<string> onlyB = b.Where(x => !sa.Contains(x)).Distinct().ToList();
        Console.WriteLine();
        Console.WriteLine("只在 A 里（" + onlyA.Count + " 项）：");
        foreach (string s in onlyA.Take(40)) Console.WriteLine("  - " + s);
        if (onlyA.Count > 40) Console.WriteLine("  ...（还有 " + (onlyA.Count - 40) + " 项）");
        Console.WriteLine("只在 B 里（" + onlyB.Count + " 项）：");
        foreach (string s in onlyB.Take(40)) Console.WriteLine("  + " + s);
        if (onlyB.Count > 40) Console.WriteLine("  ...（还有 " + (onlyB.Count - 40) + " 项）");
        return 1;
    }

    private static List<string> Lines(string path)
    {
        AssemblyDefinition asm = AssemblyDefinition.ReadAssembly(path);
        List<string> l = new List<string>();
        l.Add("assembly " + asm.Name.Name + " " + asm.Name.Version);

        foreach (TypeDefinition t in Walk(asm.MainModule.Types))
        {
            l.Add("type " + t.FullName + " : " + (t.BaseType == null ? "" : t.BaseType.FullName));
            foreach (FieldDefinition f in t.Fields)
                l.Add("field " + t.FullName + "::" + f.Name + " : " + f.FieldType.FullName);

            foreach (MethodDefinition m in t.Methods)
            {
                l.Add("method " + m.FullName);
                foreach (CustomAttribute ca in m.CustomAttributes)
                    l.Add("attr " + m.FullName + " " + ca.AttributeType.FullName + " " + Fmt(ca));
                if (!m.HasBody) continue;
                foreach (Instruction ins in m.Body.Instructions)
                {
                    // 逐条 IL 都记：操作码 + 操作数（分支目标/switch 跳过 —— 那些是随
                    // 代码布局变的偏移，不携带"行为"信息）。
                    //
                    // 为什么不能只记 ldstr / call（以前就是那样）：**数值完全不进指纹**。
                    // 把 8000 改成 9000、把 `<` 改成 `>`、把 true 改成 false 这类改动，
                    // IL 里只是 ldc.i4 / brtrue 的操作数变了，旧版会把"源码改了、
                    // 发行版没重编"判成"指纹一致"—— 这层安全网就白设了。
                    if (ins.OpCode.OperandType == OperandType.ShortInlineBrTarget ||
                        ins.OpCode.OperandType == OperandType.InlineBrTarget ||
                        ins.OpCode.OperandType == OperandType.InlineSwitch)
                    {
                        l.Add("il " + m.FullName + " " + ins.OpCode.Name);
                        continue;
                    }
                    string operand = ins.Operand == null
                        ? "" : Convert.ToString(ins.Operand, CultureInfo.InvariantCulture);
                    l.Add("il " + m.FullName + " " + ins.OpCode.Name + " " + operand);
                }
            }
        }

        foreach (MemberReference mr in asm.MainModule.GetMemberReferences())
            l.Add("memberref " + mr.FullName);

        l.Sort(StringComparer.Ordinal);
        return l;
    }

    private static IEnumerable<TypeDefinition> Walk(IEnumerable<TypeDefinition> types)
    {
        foreach (TypeDefinition t in types)
        {
            yield return t;
            foreach (TypeDefinition n in Walk(t.NestedTypes)) yield return n;
        }
    }

    private static string Fmt(CustomAttribute attr)
    {
        try
        {
            return string.Join(" ", attr.ConstructorArguments.Select(FmtArg).ToArray());
        }
        catch (Exception ex)
        {
            return "(解析失败 " + ex.GetType().Name + ")";
        }
    }

    private static string FmtArg(CustomAttributeArgument arg)
    {
        CustomAttributeArgument[] arr = arg.Value as CustomAttributeArgument[];
        if (arr != null) return "[" + string.Join(",", arr.Select(FmtArg).ToArray()) + "]";
        TypeReference tr = arg.Value as TypeReference;
        if (tr != null) return tr.Name;
        return arg.Value == null ? "null" : arg.Value.ToString();
    }

    private static string Digest(List<string> lines)
    {
        StringBuilder sb = new StringBuilder();
        foreach (string s in lines) sb.Append(s).Append('\n');
        byte[] h = SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
        StringBuilder hex = new StringBuilder();
        foreach (byte x in h) hex.Append(x.ToString("x2"));
        return hex.ToString();
    }
}
