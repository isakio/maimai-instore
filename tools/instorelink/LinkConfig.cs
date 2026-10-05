// InStoreLink —— 配置读取
//
// 只认 WorldLink.toml 里的三个键：
//   LobbyUrl = "http://isakio.cn:20100"   大厅地址（必填，缺省用我们的公共大厅）
//   RelayUrl = "isakio.cn:20101"          可选：不走大厅 /info，直接指定中继
//   Debug    = false                       可选：打详细日志
//
// 上游用的是 Tomlet（一个 TOML 库）+ 反射，我们这里手写一个够用的解析器，
// 目标是零依赖：只要 csc.exe 就能编（见 tools/build_instorelink.ps1）。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace InStoreLink
{
    public class LinkConfig
    {
        public const string DefaultLobbyUrl = "http://isakio.cn:20100";
        public const int DefaultRelayPort = 20101;

        public string LobbyUrl = DefaultLobbyUrl;
        public string RelayUrl = "";
        public bool Debug = false;

        /// <summary>读配置。文件不存在/读不动都不抛异常，用默认值继续，并把情况交给调用方打日志。</summary>
        public static LinkConfig Load(string path, out string note)
        {
            LinkConfig cfg = new LinkConfig();
            note = null;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                note = "没找到配置文件 " + path + "，用默认大厅 " + DefaultLobbyUrl;
                return cfg;
            }

            try
            {
                Dictionary<string, string> kv = ParseSimpleToml(File.ReadAllLines(path));
                string v;
                if (kv.TryGetValue("lobbyurl", out v) && v.Length > 0) cfg.LobbyUrl = v;
                if (kv.TryGetValue("relayurl", out v)) cfg.RelayUrl = v;
                if (kv.TryGetValue("debug", out v)) cfg.Debug = ParseBool(v);
            }
            catch (Exception ex)
            {
                note = "配置文件读取失败（" + ex.Message + "），用默认大厅 " + DefaultLobbyUrl;
            }
            return cfg;
        }

        /// <summary>
        /// 把 RelayUrl 解析成 host/port。支持
        /// "host"、"host:port"、"http://host:port"、"https://host" 四种写法。
        /// </summary>
        public static bool TryParseRelay(string text, out string host, out int port)
        {
            host = null;
            port = DefaultRelayPort;
            if (string.IsNullOrEmpty(text)) return false;

            string s = text.Trim();
            int scheme = s.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) s = s.Substring(scheme + 3);
            int slash = s.IndexOf('/');
            if (slash >= 0) s = s.Substring(0, slash);          // 去掉路径

            int colon = s.LastIndexOf(':');
            if (colon > 0)
            {
                string p = s.Substring(colon + 1);
                if (p.Length > 0)
                {
                    int parsed;
                    if (!int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) return false;
                    port = parsed;
                }
                s = s.Substring(0, colon);
            }
            if (s.Length == 0) return false;
            host = s;
            return true;
        }

        /// <summary>极简 TOML：只处理 key = value（值可带引号），忽略注释/空行/表头。够用就好。</summary>
        private static Dictionary<string, string> ParseSimpleToml(string[] lines)
        {
            Dictionary<string, string> kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in lines)
            {
                string line = StripComment(raw).Trim();
                if (line.Length == 0 || line.StartsWith("[", StringComparison.Ordinal)) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim().Trim('"').ToLowerInvariant();
                string val = line.Substring(eq + 1).Trim();
                if (val.Length >= 2 &&
                    ((val[0] == '"' && val[val.Length - 1] == '"') ||
                     (val[0] == '\'' && val[val.Length - 1] == '\'')))
                {
                    val = val.Substring(1, val.Length - 2);
                }
                kv[key] = val;
            }
            return kv;
        }

        /// <summary>去掉行内注释（# 在引号里的话不算）。</summary>
        private static string StripComment(string line)
        {
            bool inQuote = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"' || c == '\'') inQuote = !inQuote;
                else if (c == '#' && !inQuote) return line.Substring(0, i);
            }
            return line;
        }

        private static bool ParseBool(string v)
        {
            if (string.IsNullOrEmpty(v)) return false;
            string s = v.Trim().ToLowerInvariant();
            return s == "true" || s == "1" || s == "yes" || s == "on";
        }
    }
}
