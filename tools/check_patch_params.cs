// 检查"补丁方法的参数名"和"游戏里目标方法的参数名"是否一致。
//
// 为什么需要它：Harmony 给补丁传参是**按名字**找的（只有 __instance / __result /
// ___字段 这些特殊名字例外）。名字写错了不会编译报错，跑起来才抛
//   Parameter "socket" not found in method void PartyLink.NFSocket::.ctor(Socket nfSocket)
// 而 Harmony 又把它包成一句没头没尾的 "IL Compile Error (unknown location)"。
// 这个坑在我们这儿真踩过（把 nfSocket 写成了 socket），所以做成自动检查。
//
//   csc /r:Mono.Cecil.dll /out:check_patch_params.exe check_patch_params.cs
//   check_patch_params.exe <我们的dll> <游戏 Assembly-CSharp.dll>
//
// 退出码 0 = 全部对得上。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mono.Cecil;

public static class CheckPatchParams
{
    private static int _bad;
    private static int _checked;

    public static int Main(string[] argv)
    {
        try { Console.OutputEncoding = new UTF8Encoding(false); } catch (Exception) { }
        if (argv.Length < 2)
        {
            Console.WriteLine("用法：check_patch_params.exe <我们的 dll> <游戏的 Assembly-CSharp.dll>");
            return 2;
        }

        DefaultAssemblyResolver resolver = new DefaultAssemblyResolver();
        string gameDir = Path.GetDirectoryName(Path.GetFullPath(argv[1]));
        resolver.AddSearchDirectory(gameDir);
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(argv[0])));
        // 特性里用到 HarmonyLib.MethodType，得能解析 0Harmony（在 <游戏>\MelonLoader\net35）
        resolver.AddSearchDirectory(Path.GetFullPath(Path.Combine(gameDir, "..", "..", "MelonLoader", "net35")));
        resolver.AddSearchDirectory(Path.GetFullPath(Path.Combine(gameDir, "..", "..", "MelonLoader", "net6")));
        ReaderParameters rp = new ReaderParameters { AssemblyResolver = resolver };

        AssemblyDefinition ours = AssemblyDefinition.ReadAssembly(argv[0], rp);
        AssemblyDefinition game = AssemblyDefinition.ReadAssembly(argv[1], rp);

        Console.WriteLine("补丁程序集：" + ours.Name.Name);
        Console.WriteLine("游戏程序集：" + game.Name.Name);
        Console.WriteLine();

        foreach (TypeDefinition type in ours.MainModule.Types)
        {
            foreach (MethodDefinition patch in type.Methods)
            {
                CustomAttribute attr = patch.CustomAttributes
                    .FirstOrDefault(a => a.AttributeType.Name == "HarmonyPatch");
                if (attr == null) continue;
                try
                {
                    Check(resolver, game, type, patch, attr);
                }
                catch (Exception ex)
                {
                    // 注意：Cecil 的某些异常 ToString() 都会炸，所以只打 Message
                    _bad++;
                    Console.WriteLine("✗ " + type.Name + "." + patch.Name + " 检查时出错："
                                      + ex.GetType().Name + " —— " + SafeMessage(ex));
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine(_bad == 0
            ? "全部对得上（检查了 " + _checked + " 条补丁）"
            : "有 " + _bad + " 条对不上（共 " + _checked + " 条）");
        return _bad == 0 ? 0 : 1;
    }

    private static void Check(IAssemblyResolver resolver, AssemblyDefinition game,
        TypeDefinition patchType, MethodDefinition patch, CustomAttribute attr)
    {
        _checked++;
        string patchName = patchType.Name + "." + patch.Name;

        // [HarmonyPatch(typeof(X), "方法名")] / (typeof(X), MethodType.Ctor) / (typeof(X), "属性名", MethodType.Getter)
        List<CustomAttributeArgument> args = attr.ConstructorArguments.ToList();
        TypeReference declaringType = args.Count > 0 ? args[0].Value as TypeReference : null;
        if (declaringType == null)
        {
            Console.WriteLine("? " + patchName + "：特性里没写目标类型，跳过");
            return;
        }
        string declaringName = declaringType.FullName.Replace("/", "+");
        // 目标可能在别的程序集里（AMDaemon.NET、UnityEngine……），所以直接让 Cecil 解析
        TypeDefinition gameType = null;
        try { gameType = declaringType.Resolve(); }
        catch (Exception) { }
        if (gameType == null) gameType = FindType(game, declaringName);
        if (gameType == null)
        {
            Report(false, patchName, "找不到类型 " + declaringName);
            return;
        }

        MethodType kind = MethodType.Normal;
        string targetName = null;
        TypeReference[] argumentTypes = null;
        foreach (CustomAttributeArgument a in args.Skip(1))
        {
            string asString = a.Value as string;
            TypeReference asType = a.Value as TypeReference;
            CustomAttributeArgument[] asArray = a.Value as CustomAttributeArgument[];
            if (asString != null)
            {
                targetName = asString;
            }
            else if (asType != null)
            {
                argumentTypes = new[] { asType };
            }
            else if (asArray != null)
            {
                argumentTypes = asArray.Select(x => x.Value as TypeReference)
                    .Where(x => x != null).ToArray();
            }
            else if (a.Type.FullName == "HarmonyLib.MethodType")
            {
                kind = (MethodType)Convert.ToInt32(a.Value);
            }
            else if (a.Value is bool)
            {
                targetName = (bool)a.Value ? ".ctor" : ".cctor";
            }
        }

        MethodDefinition target = ResolveTarget(gameType, targetName, kind, argumentTypes);
        if (target == null)
        {
            Report(false, patchName, "找不到目标方法（名字/类型对不上？）");
            return;
        }

        // 逐个比对参数名：只比"普通参数"（跳过 __instance/__result/___字段 这些特殊名字）
        List<string> mismatches = new List<string>();
        foreach (ParameterDefinition p in patch.Parameters)
        {
            string name = p.Name;
            if (name.StartsWith("_", StringComparison.Ordinal)) continue;      // Harmony 的特殊参数
            if (name.StartsWith("__", StringComparison.Ordinal)) continue;
            bool found = target.Parameters.Any(tp => tp.Name == name && TypeName(tp.ParameterType) == TypeName(p.ParameterType));
            if (!found)
            {
                bool sameNameWrongType = target.Parameters.Any(tp => tp.Name == name);
                mismatches.Add(name + (sameNameWrongType ? "（名字对但类型不同）" : "（目标里没有这个参数名）"));
            }
        }

        if (mismatches.Count == 0)
        {
            Console.WriteLine("✓ " + patchName + " → " + gameType.Name + "." + target.Name);
        }
        else
        {
            Report(false, patchName,
                "→ " + gameType.Name + "." + target.Name + "：" + string.Join("，", mismatches.ToArray())
                + "　目标参数表：(" + string.Join(", ", target.Parameters
                    .Select(p => p.ParameterType.Name + " " + p.Name).ToArray()) + ")");
        }
    }

    private static void Report(bool ok, string what, string detail)
    {
        if (ok) return;
        _bad++;
        Console.WriteLine("✗ " + what + " " + detail);
    }

    private static string SafeMessage(Exception ex)
    {
        try { return ex.Message; }
        catch (Exception) { return "(连 Message 都取不到)"; }
    }

    private static string TypeName(TypeReference t)
    {
        if (t.IsByReference) return TypeName(t.GetElementType()) + "&";
        if (t.IsArray) return TypeName(t.GetElementType()) + "[]";
        return t.Name;
    }

    private enum MethodType { Normal = 0, Getter = 1, Setter = 2, Constructor = 3, StaticConstructor = 4 }

    private static MethodDefinition ResolveTarget(TypeDefinition type, string name, MethodType kind,
        TypeReference[] argumentTypes)
    {
        if (kind == MethodType.Constructor || kind == MethodType.StaticConstructor)
        {
            return type.Methods.FirstOrDefault(m =>
                m.IsConstructor && (argumentTypes == null || SameArgs(m, argumentTypes)));
        }
        if (kind == MethodType.Getter)
        {
            PropertyDefinition prop = type.Properties.FirstOrDefault(p => p.Name == name);
            return prop == null ? null : prop.GetMethod;
        }
        if (kind == MethodType.Setter)
        {
            PropertyDefinition prop = type.Properties.FirstOrDefault(p => p.Name == name);
            return prop == null ? null : prop.SetMethod;
        }
        return type.Methods.FirstOrDefault(m =>
            m.Name == name && (argumentTypes == null || SameArgs(m, argumentTypes)));
    }

    private static bool SameArgs(MethodDefinition m, TypeReference[] argumentTypes)
    {
        if (m.Parameters.Count != argumentTypes.Length) return false;
        for (int i = 0; i < argumentTypes.Length; i++)
        {
            if (TypeName(m.Parameters[i].ParameterType) != TypeName(argumentTypes[i])) return false;
        }
        return true;
    }

    private static TypeDefinition FindType(AssemblyDefinition asm, string fullName)
    {
        foreach (ModuleDefinition m in asm.Modules)
        {
            TypeDefinition t = m.GetType(fullName);
            if (t != null) return t;
            foreach (TypeDefinition nested in m.Types.SelectMany(AllNested))
            {
                if (nested.FullName.Replace("/", "+") == fullName) return nested;
            }
        }
        return null;
    }

    private static IEnumerable<TypeDefinition> AllNested(TypeDefinition t)
    {
        foreach (TypeDefinition n in t.NestedTypes)
        {
            yield return n;
            foreach (TypeDefinition nn in AllNested(n)) yield return nn;
        }
    }
}
