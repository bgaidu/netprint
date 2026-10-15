using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace PrintServer
{
    /// <summary>
    /// 打印队列管理：枚举队列任务、取消任务、清空队列。
    /// 基于 winspool.drv 的 EnumJobs / SetJob。
    /// </summary>
    internal static class PrintQueueService
    {
        // ===== winspool P/Invoke =====

        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool ClosePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool EnumJobs(IntPtr hPrinter, int FirstJob, int NoJobs, int Level,
            IntPtr pJob, int cbBuf, out int pcbNeeded, out int pcReturned);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool SetJob(IntPtr hPrinter, int JobId, int Level,
            IntPtr pJob, int Command);

        // SetJob 命令
        private const int JOB_CONTROL_CANCEL = 3;
        private const int JOB_CONTROL_DELETE = 5;

        // 任务状态文本
        private static readonly string[] JobStatusText = {
            "未知",          // 0
            "暂停",          // 1 JOB_STATUS_PAUSED
            "错误",          // 2 JOB_STATUS_ERROR
            "删除中",        // 4 JOB_STATUS_DELETING
            "拼合",          // 8 JOB_STATUS_SPOOLING
            "正在打印",      // 16 JOB_STATUS_PRINTING
            "已脱机",        // 32 JOB_STATUS_OFFLINE
            "已连接",        // 64 JOB_STATUS_PAPEROUT
            "已打印",        // 128 JOB_STATUS_PRINTED
            "已取消",        // 256 JOB_STATUS_DELETED
            "阻止中",        // 512 JOB_STATUS_BLOCKED_DEVQ
            "用户干预",      // 1024 JOB_STATUS_USER_INTERVENTION
            "重启中",        // 2048 JOB_STATUS_RESTART
        };

        /// <summary>查询打印机队列任务，返回 JSON 数组。</summary>
        public static string GetQueueJson(string printerName)
        {
            var sb = new StringBuilder("[");
            bool first = true;
            try
            {
                IntPtr hPrinter;
                if (!OpenPrinter(printerName, out hPrinter, IntPtr.Zero))
                    return "[]";

                try
                {
                    // 第一次调用获取所需缓冲区大小
                    int needed, returned;
                    if (!EnumJobs(hPrinter, 0, 1, 1, IntPtr.Zero, 0, out needed, out returned))
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (err != 122 /* ERROR_INSUFFICIENT_BUFFER */ && needed == 0)
                            return "[]";
                    }

                    if (needed <= 0) return "[]";

                    IntPtr buf = Marshal.AllocHGlobal(needed);
                    try
                    {
                        int returned2;
                        if (!EnumJobs(hPrinter, 0, 1024, 1, buf, needed, out needed, out returned2))
                            return "[]";

                        // JOB_INFO_1 结构 (x64):
                        //   JobId int @0
                        //   pPrinterName ptr @8
                        //   pMachineName ptr @16
                        //   pUserName ptr @24
                        //   pDocument ptr @32
                        //   pDatatype ptr @40
                        //   pStatus ptr @48
                        //   Status int @56
                        //   Priority int @60
                        //   Position int @64
                        //   TotalPages int @68
                        //   PagesPrinted int @72
                        //   Submitted SYSTEMTIME @80 (16 bytes)
                        int structSize = IntPtr.Size == 8 ? 96 : 80;

                        for (int i = 0; i < returned2; i++)
                        {
                            IntPtr jobPtr = IntPtr.Add(buf, i * structSize);
                            int jobId = Marshal.ReadInt32(jobPtr, 0);
                            IntPtr docPtr = Marshal.ReadIntPtr(jobPtr, IntPtr.Size == 8 ? 32 : 16);
                            IntPtr userPtr = Marshal.ReadIntPtr(jobPtr, IntPtr.Size == 8 ? 24 : 12);
                            int status = Marshal.ReadInt32(jobPtr, IntPtr.Size == 8 ? 56 : 28);
                            int totalPages = Marshal.ReadInt32(jobPtr, IntPtr.Size == 8 ? 68 : 36);

                            string doc = docPtr == IntPtr.Zero ? "" : Marshal.PtrToStringUni(docPtr);
                            string user = userPtr == IntPtr.Zero ? "" : Marshal.PtrToStringUni(userPtr);

                            if (!first) sb.Append(',');
                            first = false;
                            sb.Append("{\"id\":").Append(jobId)
                              .Append(",\"document\":").Append(WebServer.JsonEncode(doc))
                              .Append(",\"user\":").Append(WebServer.JsonEncode(user))
                              .Append(",\"status\":").Append(StatusText(status))
                              .Append(",\"pages\":").Append(totalPages).Append('}');
                        }
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
                finally { ClosePrinter(hPrinter); }
            }
            catch { }
            sb.Append(']');
            return sb.ToString();
        }

        /// <summary>取消/删除指定打印机上的任务；返回是否成功。</summary>
        public static bool CancelJob(string printerName, int jobId)
        {
            IntPtr hPrinter;
            if (!OpenPrinter(printerName, out hPrinter, IntPtr.Zero)) return false;
            try
            {
                // 先取消，再删除（彻底移除）
                SetJob(hPrinter, jobId, 0, IntPtr.Zero, JOB_CONTROL_CANCEL);
                SetJob(hPrinter, jobId, 0, IntPtr.Zero, JOB_CONTROL_DELETE);
                return true;
            }
            catch { return false; }
            finally { ClosePrinter(hPrinter); }
        }

        /// <summary>清空指定打印机的整个队列；返回被清除的任务数。</summary>
        public static int ClearQueue(string printerName)
        {
            IntPtr hPrinter;
            if (!OpenPrinter(printerName, out hPrinter, IntPtr.Zero)) return 0;
            try
            {
                int needed, returned;
                EnumJobs(hPrinter, 0, 1, 1, IntPtr.Zero, 0, out needed, out returned);
                if (needed <= 0) return 0;

                IntPtr buf = Marshal.AllocHGlobal(needed);
                int cleared = 0;
                try
                {
                    int returned2;
                    if (!EnumJobs(hPrinter, 0, 1024, 1, buf, needed, out needed, out returned2))
                        return 0;

                    int structSize = IntPtr.Size == 8 ? 96 : 80;
                    var jobIds = new List<int>();
                    for (int i = 0; i < returned2; i++)
                        jobIds.Add(Marshal.ReadInt32(IntPtr.Add(buf, i * structSize), 0));

                    foreach (var id in jobIds)
                    {
                        SetJob(hPrinter, id, 0, IntPtr.Zero, JOB_CONTROL_CANCEL);
                        SetJob(hPrinter, id, 0, IntPtr.Zero, JOB_CONTROL_DELETE);
                        cleared++;
                    }
                    return cleared;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            catch { return 0; }
            finally { ClosePrinter(hPrinter); }
        }

        private static int StatusText(int status)
        {
            if (status == 0) return 0; // 空闲
            if ((status & 16) != 0) return 5;  // 打印中
            if ((status & 8) != 0) return 4;   // 排队
            if ((status & 1) != 0) return 1;   // 暂停
            if ((status & 128) != 0) return 8; // 已打印
            if ((status & 2) != 0) return 2;   // 错误
            if ((status & 256) != 0) return 9; // 已取消
            return 0;
        }
    }
}