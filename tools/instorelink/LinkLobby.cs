// InStoreLink —— 大厅 HTTP 客户端
//
// 三个接口（格式和上游 worldlinkd / 我们的 instorematchd 都一样）：
//   GET  /info           → {"relayHost":"isakio.cn","relayPort":20101}
//   GET  /online         → {"totalUsers":2,"activeRecruits":1}
//   GET  /recruit/list   → 每行一条 JSON（房主上传的招募数据，不含 Keychip）
//   POST /recruit/start  → {"Keychip":"W...","RecruitInfo":{...}}
//   POST /recruit/finish → 同上（关房）
//
// 用 WebClient + 线程回调，和上游一致（.NET Framework 4.0 上没有 HttpClient）。
// 所有方法都不抛异常，把错误交给回调，避免把游戏线程带崩。

using System;
using System.Net;
using System.Text;

namespace InStoreLink
{
    public static class LinkLobby
    {
        private const int TimeoutMs = 8000;

        public static string Combine(string baseUrl, string path)
        {
            if (string.IsNullOrEmpty(baseUrl)) return null;
            string b = baseUrl.TrimEnd('/');
            return b + path;
        }

        public static string Get(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            try
            {
                using (WebClient web = NewWebClient())
                {
                    return web.DownloadString(new Uri(url));
                }
            }
            catch (Exception ex)
            {
                LinkLog.Debug("GET " + url + " 失败：" + ex.Message);
                return null;
            }
        }

        /// <summary>异步 GET。callback(响应文本, 异常)；两者必有一个为 null。</summary>
        public static void GetAsync(string url, Action<string, Exception> callback)
        {
            if (string.IsNullOrEmpty(url))
            {
                if (callback != null) callback(null, new ArgumentNullException("url"));
                return;
            }
            try
            {
                WebClient web = NewWebClient();
                web.DownloadStringCompleted += delegate(object sender, DownloadStringCompletedEventArgs e)
                {
                    try
                    {
                        if (callback != null) callback(e.Cancelled ? null : e.Result, e.Error);
                    }
                    finally
                    {
                        web.Dispose();
                    }
                };
                web.DownloadStringAsync(new Uri(url));
            }
            catch (Exception ex)
            {
                if (callback != null) callback(null, ex);
            }
        }

        public static string Post(string url, string body)
        {
            if (string.IsNullOrEmpty(url)) return null;
            try
            {
                using (WebClient web = NewWebClient())
                {
                    web.Headers["Content-Type"] = "application/json";
                    return web.UploadString(new Uri(url), body ?? "");
                }
            }
            catch (Exception ex)
            {
                LinkLog.Debug("POST " + url + " 失败：" + ex.Message);
                return null;
            }
        }

        /// <summary>异步 POST（开房/关房用，别占着游戏线程）。</summary>
        public static void PostAsync(string url, string body, Action<string, Exception> callback)
        {
            if (string.IsNullOrEmpty(url)) return;
            try
            {
                WebClient web = NewWebClient();
                web.Headers["Content-Type"] = "application/json";
                web.UploadStringCompleted += delegate(object sender, UploadStringCompletedEventArgs e)
                {
                    try
                    {
                        if (callback != null) callback(e.Cancelled ? null : e.Result, e.Error);
                    }
                    finally
                    {
                        web.Dispose();
                    }
                };
                web.UploadStringAsync(new Uri(url), body ?? "");
            }
            catch (Exception ex)
            {
                if (callback != null) callback(null, ex);
            }
        }

        private static WebClient NewWebClient()
        {
            WebClient web = new WebClient();
            web.Encoding = new UTF8Encoding(false);
            web.Proxy = null;                        // 局域网/直连场景下别走系统代理
            return web;
        }
    }
}
