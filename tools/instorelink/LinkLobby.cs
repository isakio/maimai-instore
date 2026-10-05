// InStoreLink —— 大厅 HTTP 客户端
//
// 五个接口（格式和上游 worldlinkd / 我们的 instorematchd 都一样）：
//   GET  /info           → {"relayHost":"isakio.cn","relayPort":20101}
//   GET  /online         → {"totalUsers":2,"activeRecruits":1}
//   GET  /recruit/list   → 每行一条 JSON（房主上传的招募数据，不含 Keychip）
//   POST /recruit/start  → {"Keychip":"W...","RecruitInfo":{...}}
//   POST /recruit/finish → 同上（关房）
//
// 拉数据用 WebClient（.NET Framework 3.5/4.0 上没有 HttpClient），所有方法都不抛异常：
// 同步那个把错误变成"返回 null"，异步那个把错误交给回调，避免把游戏线程带崩。

using System;
using System.IO;
using System.Net;
using System.Text;

namespace InStoreLink
{
    public static class LinkLobby
    {
        public const int TimeoutMs = 8000;

        public static string Combine(string baseUrl, string path)
        {
            if (string.IsNullOrEmpty(baseUrl)) return null;
            string b = baseUrl.TrimEnd('/');
            return b + path;
        }

        /// <summary>
        /// 同步 GET（轮询线程用：在线人数、招募列表）。实现统一走下面那个带超时的版本 ——
        /// WebClient 自己没有超时，大厅要是卡住不回，轮询线程会一直陪着等到 TCP 自己放弃
        /// （招募列表就再也不刷新了）。
        /// </summary>
        public static string Get(string url)
        {
            return GetWithTimeout(url, TimeoutMs);
        }

        /// <summary>
        /// 同步 GET，带超时（WebClient 自己没法设超时，用 HttpWebRequest）。
        /// 成功返回响应文本，任何失败返回 null —— 调用方可以据此重试。
        /// </summary>
        public static string GetWithTimeout(string url, int timeoutMs)
        {
            if (string.IsNullOrEmpty(url)) return null;
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(new Uri(url));
                request.Method = "GET";
                request.Proxy = null;
                request.Timeout = timeoutMs;
                request.ReadWriteTimeout = timeoutMs;
                using (WebResponse response = request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream, new UTF8Encoding(false)))
                {
                    return reader.ReadToEnd();
                }
            }
            catch (Exception ex)
            {
                LinkLog.Debug("GET " + url + " 失败：" + ex.Message);
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
                        if (callback == null) return;
                        // e.Error != null 时读 e.Result 会直接把那个异常抛出来（AsyncCompletedEventArgs
                        // 的既定行为），所以必须先看 Error 再取 Result。
                        if (e.Error != null) callback(null, e.Error);
                        else if (e.Cancelled) callback(null, new OperationCanceledException(url));
                        else callback(e.Result, null);
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
