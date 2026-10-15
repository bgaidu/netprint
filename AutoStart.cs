using System;
using System.IO;
using Microsoft.Win32;

namespace PrintServer
{
    /// <summary>开机自启：写/读/删 注册表 HKCU\...\Run 键。</summary>
    internal static class AutoStart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "PrintServer";

        /// <summary>当前是否已设置开机自启。</summary>
        public static bool IsEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    var val = key?.GetValue(ValueName) as string;
                    return !string.IsNullOrEmpty(val);
                }
            }
            catch { return false; }
        }

        /// <summary>设为开机自启（写入 exe 路径）。</summary>
        public static bool Enable()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key == null) return false;
                    key.SetValue(ValueName, "\"" + ExecutablePath() + "\" --elevated");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("设置开机自启失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>取消开机自启。</summary>
        public static bool Disable()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key == null) return false;
                    key.DeleteValue(ValueName, false);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("取消开机自启失败: " + ex.Message);
                return false;
            }
        }

        private static string ExecutablePath()
        {
            // 发布目录 exe 或 bin 目录
            var exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PrintServer.exe");
            return File.Exists(exe) ? exe : System.Reflection.Assembly.GetExecutingAssembly().Location;
        }
    }
}