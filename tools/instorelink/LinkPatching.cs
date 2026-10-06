// InStoreLink —— 挂补丁的小工具
//
// 为什么不用 Harmony 的 CreateAndPatchAll(整个类)：它遇到第一条失败就抛，
// 异常只有一句 "IL Compile Error (unknown location)"，既不知道是哪条、也看不到原因，
// 而且后面那些本来能挂上的补丁**全都不挂了**（第一次实测就栽在这上面）。
//
// 这里改成一条一条挂：自己从 HarmonyPatch 特性里读出目标方法，单独 patch，
// 逐条打印成功/失败 + 完整异常。失败的那条只影响它自己，日志里直接点名。
// 代价是启动时多几十次反射调用，可以忽略。
//
// 逐条的成功行（`✓ 补丁名 → 目标方法`）**不受 Debug 开关影响**，一律打出来：
// 文档里就是让玩家拿这 34 行确认"插件在这台机器上挂上了没有"（和 InStoreMatch
// 那份打印 7 行"挂钩成功"的插件保持一致）。心跳、每个包那些才是 Debug 才有的。

using System;
using System.Reflection;
using HarmonyLib;

namespace InStoreLink
{
    public static class LinkPatching
    {
        /// <summary>
        /// 逐条挂补丁，返回成功条数。
        ///
        /// 为什么不用 Harmony 的 CreateAndPatchAll(整类)：它遇到第一条失败就抛，
        /// 异常只有一句 "IL Compile Error (unknown location)"，既不知道是哪条、也看不到原因，
        /// 而且后面那些本来能挂上的补丁全都不挂了。逐条挂就没这问题：
        /// 失败的那条只影响它自己，日志里点名道姓。
        /// </summary>
        public static int ApplyAll(string harmonyId, Type patchClass, string label)
        {
            HarmonyLib.Harmony harmony = new HarmonyLib.Harmony(harmonyId);
            int ok = 0, bad = 0;
            foreach (MethodInfo patch in PatchMethods(patchClass))
            {
                MethodBase target = ResolveTarget(patch);
                if (target == null)
                {
                    // 目标方法都找不到（比如游戏更新后改名了）
                    bad++;
                    LinkLog.Error("  ✗ " + patch.Name + "：没找到目标方法 " + DescribeTarget(patch));
                    continue;
                }

                try
                {
                    HarmonyMethod prefix = IsPrefix(patch) ? new HarmonyMethod(patch) : null;
                    HarmonyMethod postfix = IsPrefix(patch) ? null : new HarmonyMethod(patch);
                    harmony.Patch(target, prefix, postfix);
                    ok++;
                    // 用 Msg：这一行是给玩家/排查用的验收信息，不能只在 Debug 下出现
                    LinkLog.Msg("  ✓ " + patch.Name + " → "
                                + target.DeclaringType.Name + "." + target.Name);
                }
                catch (Exception ex)
                {
                    bad++;
                    string inner = ex.InnerException == null ? "-" : ex.InnerException.Message;
                    LinkLog.Error("  ✗ " + patch.Name + " → "
                                  + target.DeclaringType.Name + "." + target.Name
                                  + "：" + ex.GetType().Name + " / " + ex.Message + " / 内层：" + inner);
                    if (LinkLog.Verbose) LinkLog.Error(ex.ToString());
                }
            }
            LinkLog.Msg(label + "：成功 " + ok + " 条" + (bad > 0 ? "，失败 " + bad + " 条" : ""));
            return ok;
        }

        // ---------------------------------------------------------------- 反射工具

        private static MethodInfo[] PatchMethods(Type patchClass)
        {
            MethodInfo[] all = patchClass.GetMethods(BindingFlags.Public | BindingFlags.Static);
            System.Collections.Generic.List<MethodInfo> list = new System.Collections.Generic.List<MethodInfo>();
            foreach (MethodInfo m in all)
            {
                if (m.GetCustomAttributes(typeof(HarmonyPatch), true).Length == 0) continue;
                list.Add(m);
            }
            return list.ToArray();
        }

        private static bool IsPrefix(MethodInfo patch)
        {
            return patch.GetCustomAttributes(typeof(HarmonyPrefix), true).Length > 0;
        }

        /// <summary>从 [HarmonyPatch] 特性里把目标方法解析出来。</summary>
        private static MethodBase ResolveTarget(MethodInfo patch)
        {
            object attr = null;
            object[] attrs = patch.GetCustomAttributes(typeof(HarmonyPatch), true);
            if (attrs.Length == 0) return null;
            attr = attrs[0];

            // HarmonyAttribute（HarmonyPatch 的基类）里有个公开字段 info
            FieldInfo infoField = attr.GetType().GetField("info",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            HarmonyMethod info = infoField == null ? null : infoField.GetValue(attr) as HarmonyMethod;
            if (info == null || info.declaringType == null) return null;

            Type[] args = info.argumentTypes;
            if (info.methodType == MethodType.Constructor)
                return AccessTools.Constructor(info.declaringType, args);
            if (info.methodType == MethodType.Getter || info.methodType == MethodType.Setter)
            {
                PropertyInfo prop = AccessTools.Property(info.declaringType, info.methodName);
                if (prop == null) return null;
                return info.methodType == MethodType.Getter ? prop.GetGetMethod(true) : prop.GetSetMethod(true);
            }
            return AccessTools.Method(info.declaringType, info.methodName, args);
        }

        private static string DescribeTarget(MethodInfo patch)
        {
            object[] attrs = patch.GetCustomAttributes(typeof(HarmonyPatch), true);
            if (attrs.Length == 0) return "?";
            FieldInfo infoField = attrs[0].GetType().GetField("info",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            HarmonyMethod info = infoField == null ? null : infoField.GetValue(attrs[0]) as HarmonyMethod;
            if (info == null) return "?";
            return (info.declaringType == null ? "?" : info.declaringType.Name) + "." + info.methodName +
                   (info.argumentTypes == null || info.argumentTypes.Length == 0
                       ? "()"
                       : "(" + string.Join(",", Array.ConvertAll(info.argumentTypes, t => t.Name)) + ")");
        }
    }
}
