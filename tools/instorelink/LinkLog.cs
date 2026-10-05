// InStoreLink —— 日志
//
// MelonLoader 会自动给每条日志加 "[InStoreLink] " 前缀，所以这里只管级别和内容。
// Debug 打开时才打 Debug/Info（和上游一样：Info 也算"详细日志"，会刷屏）。

using MelonLoader;

namespace InStoreLink
{
    public static class LinkLog
    {
        public static bool Verbose;

        public static void Error(string msg)
        {
            MelonLogger.Error(msg);
        }

        public static void Warn(string msg)
        {
            MelonLogger.Warning(msg);
        }

        public static void Msg(string msg)
        {
            MelonLogger.Msg(msg);
        }

        public static void Debug(string msg)
        {
            if (!Verbose) return;
            MelonLogger.Msg("[DEBUG] " + msg);
        }

        public static void Info(string msg)
        {
            if (!Verbose) return;
            MelonLogger.Msg(msg);
        }
    }
}
