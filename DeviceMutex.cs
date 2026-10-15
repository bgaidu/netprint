using System;
using System.Threading;

namespace PrintServer
{
    /// <summary>
    /// 打印/扫描设备互斥管理器。
    /// 防止打印与扫描同时操作同一台设备（尤其 USB 多功能一体机），
    /// 后发操作等待前一个完成，超时则返回"设备忙"。
    /// </summary>
    internal static class DeviceMutex
    {
        private static readonly object Gate = new object();
        private static volatile bool _printing;
        private static volatile bool _scanning;
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

        public static bool IsPrinting => _printing;
        public static bool IsScanning => _scanning;
        public static bool IsBusy => _printing || _scanning;

        /// <summary>尝试获取"打印权"。得到 true 后必须调用 ReleasePrint()。</summary>
        public static bool TryEnterPrint(TimeSpan? timeout = null)
        {
            lock (Gate)
            {
                // 扫描中则等待
                var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
                while (_scanning)
                {
                    var remain = deadline - DateTime.UtcNow;
                    if (remain <= TimeSpan.Zero) return false;
                    Monitor.Wait(Gate, remain);
                }
                _printing = true;
                return true;
            }
        }

        /// <summary>释放打印权（必须在 TryEnterPrint true 后调用）。</summary>
        public static void ReleasePrint()
        {
            lock (Gate)
            {
                _printing = false;
                Monitor.PulseAll(Gate);
            }
        }

        /// <summary>尝试获取"扫描权"。得到 true 后必须调用 ReleaseScan()。</summary>
        public static bool TryEnterScan(TimeSpan? timeout = null)
        {
            lock (Gate)
            {
                var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
                while (_printing)
                {
                    var remain = deadline - DateTime.UtcNow;
                    if (remain <= TimeSpan.Zero) return false;
                    Monitor.Wait(Gate, remain);
                }
                _scanning = true;
                return true;
            }
        }

        /// <summary>释放扫描权。</summary>
        public static void ReleaseScan()
        {
            lock (Gate)
            {
                _scanning = false;
                Monitor.PulseAll(Gate);
            }
        }
    }
}