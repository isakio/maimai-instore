// 用 Mono.Cecil 打印一个 .NET 程序集里某几个类型的方法签名。
// 用途：对比"上游发布版 WorldLink.dll"和"我们编出来的 InStoreLink.dll"里
//       补丁方法的参数类型（源码和发布版不一致时，只有这里看得出来）。
//
//   csc /r:Mono.Cecil.dll /out:dump_sigs.exe dump_sigs.cs
//   dump_sigs.exe <dll 路径> [类型名过滤...]

using System;
using System.Linq;
using System.Text;
using Mono.Cecil;

public static class DumpSigs
{
    public static int Main(string[] argv)
    {
        try { Console.OutputEncoding = new UTF8Encoding(false); } catch (Exception) { }
        if (argv.Length < 1)
        {
            Console.WriteLine("用法：dump_sigs.exe <dll> [类型名过滤...]");
            return 2;
        }
        string[] filters = argv.Skip(1).ToArray();
        AssemblyDefinition asm = AssemblyDefinition.ReadAssembly(argv[0]);
        Console.WriteLine("程序集：" + asm.Name.Name + " " + asm.Name.Version);
        foreach (TypeDefinition t in asm.MainModule.Types)
        {
            if (filters.Length > 0 && !filters.Any(f => t.Name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0))
                continue;
            Console.WriteLine();
            Console.WriteLine("== " + t.FullName);
            foreach (MethodDefinition m in t.Methods)
            {
                try
                {
                    string args = string.Join(", ", m.Parameters
                        .Select(p => p.ParameterType.FullName + " " + p.Name).ToArray());
                    string attrs = m.CustomAttributes.Any(a => a.AttributeType.Name == "HarmonyPrefix") ? "[Prefix]"
                                 : m.CustomAttributes.Any(a => a.AttributeType.Name == "HarmonyPostfix") ? "[Postfix]" : "";
                    string patch = "";
                    CustomAttribute hp = m.CustomAttributes.FirstOrDefault(a => a.AttributeType.Name == "HarmonyPatch");
                    if (hp != null) patch = "  <- " + SafeFmt(hp);
                    Console.WriteLine("   " + attrs + " " + m.Name + "(" + args + ") : " + m.ReturnType.Name + patch);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("   ? " + m.Name + "（打印失败：" + ex.GetType().Name + " " + ex.Message + "）");
                }
            }
        }
        return 0;
    }

    private static string Fmt(CustomAttributeArgument arg)
    {
        CustomAttributeArgument[] arr = arg.Value as CustomAttributeArgument[];
        if (arr != null)
            return "[" + string.Join(",", arr.Select(x => Fmt(x)).ToArray()) + "]";
        TypeReference tr = arg.Value as TypeReference;
        if (tr != null) return tr.Name;
        return arg.Value == null ? "null" : arg.Value.ToString();
    }

    private static string SafeFmt(CustomAttribute attr)
    {
        try
        {
            return string.Join(" ", attr.ConstructorArguments.Select(c => Fmt(c)).ToArray());
        }
        catch (Exception ex)
        {
            return "(特性解析失败: " + ex.GetType().Name + ")";
        }
    }
}
