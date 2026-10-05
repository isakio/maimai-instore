// InStoreLink —— MelonLoader 入口
//
// 跟 InStoreMatch（我们那个补分类栏的插件）是一套东西：
//   InStoreMatch.dll  → 让选曲界面出现「店内マッチング」这一格
//   InStoreLink.dll   → 把那一格接到公网（本文件；替代上游的 WorldLink.dll）
//   instorematchd     → 服务端（大厅 + 中继）
//
// 构建：tools/build_instorelink.ps1（Windows 自带的 csc.exe，C# 5，不需要 SDK）

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MelonLoader;

[assembly: MelonInfo(typeof(InStoreLink.LinkMod), "InStoreLink", "0.3.0", "isakio")]
[assembly: MelonGame("sega-interactive", "Sinmai")]
// 和上游一样：不让 MelonLoader 自动扫特性去挂补丁，全部由 OnInitializeMelon 手动挂，
// 这样每条补丁的成败都在我们自己的日志里（也避免同一条被挂两遍）。
[assembly: HarmonyDontPatchAll]

namespace InStoreLink
{
    public class LinkMod : MelonMod
    {
        public const string ConfigFile = "InStoreLink.toml";
        /// <summary>老的配置名（上游 WorldLink 用的那份），找不到新名字时回退到它。</summary>
        public const string LegacyConfigFile = "WorldLink.toml";
        public const string Id = "cn.isakio.instorelink";

        public override void OnInitializeMelon()
        {
            string note;
            string configPath = LocateConfig();
            LinkRuntime.Config = LinkConfig.Load(configPath, out note);
            LinkLog.Verbose = LinkRuntime.Config.Debug;

            LinkLog.Msg("InStoreLink 0.3.0 已加载（配置 " + configPath + "，大厅 " + LinkRuntime.Config.LobbyUrl +
                        "，详细日志 " + (LinkRuntime.Config.Debug ? "开" : "关") + "）");
            if (!string.IsNullOrEmpty(note)) LinkLog.Warn(note);

            LinkRuntime.BeforePatch();

            int total = 0;
            total += LinkPatching.ApplyAll(Id, typeof(PatchesNet), "通信层");
            total += LinkPatching.ApplyAll(Id, typeof(PatchesParty), "招募/选曲");
            LinkLog.Msg("挂钩完成，共 " + total + " 条生效");
        }

        /// <summary>
        /// 找配置文件：先按相对路径找（Unity 游戏的当前目录正常就是游戏根目录），
        /// 再用进程主程序所在目录（Sinmai.exe 那一层，等于游戏根目录）拼一遍 ——
        /// 有些启动器会把当前目录设到别处，那时相对路径找不到会静默回落默认大厅
        /// （自建大厅的人会莫名其妙连到我们这台）。
        /// </summary>
        private static string LocateConfig()
        {
            string root = null;
            try { root = AppDomain.CurrentDomain.BaseDirectory; }
            catch (Exception) { }

            if (File.Exists(ConfigFile)) return ConfigFile;
            if (!string.IsNullOrEmpty(root))
            {
                string full = Path.Combine(root, ConfigFile);
                if (File.Exists(full)) return full;
            }

            if (File.Exists(LegacyConfigFile))
            {
                LinkLog.Msg("没找到 " + ConfigFile + "，先用老的 " + LegacyConfigFile);
                return LegacyConfigFile;
            }
            if (!string.IsNullOrEmpty(root))
            {
                string legacyFull = Path.Combine(root, LegacyConfigFile);
                if (File.Exists(legacyFull))
                {
                    LinkLog.Msg("没找到 " + ConfigFile + "，先用老的 " + legacyFull);
                    return legacyFull;
                }
            }

            // 都没有：交给 LinkConfig.Load 去解释（它会打一句提示，然后走默认大厅）
            return ConfigFile;
        }

        public override void OnApplicationQuit()
        {
            LinkRuntime.Stop();
        }

    }
}
