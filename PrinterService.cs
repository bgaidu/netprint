using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Printing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PrintServer
{
    /// <summary>打印机服务：枚举、打印（图片/文本，RAW 方式支持双面、份数、纸张）。</summary>
    internal static class PrinterService
    {
        public static string LastError = "";

        /// <summary>打印互斥锁：所有打印请求全局串行，避免并发的不同打印机任务串台。</summary>
        private static readonly object PrintLock = new object();

        // ===== winspool P/Invoke（打印机状态查询）=====

        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool GetPrinter(IntPtr hPrinter, int Level, IntPtr pPrinter, int cbBuf, out int pcbNeeded);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool ClosePrinter(IntPtr hPrinter);

        /// <summary>枚举本机打印机（排除虚拟打印机），返回 JSON 数组字符串（含状态/端口/驱动）。</summary>
        public static string EnumeratePrinters()
        {
            var sb = new StringBuilder("[");
            bool first = true;
            foreach (string printer in PrinterSettings.InstalledPrinters)
            {
                if (IsVirtualPrinter(printer)) continue;
                if (!first) sb.Append(',');
                first = false;
                var status = GetPrinterInfo(printer);
                sb.Append("{\"name\":").Append(WebServer.JsonEncode(printer))
                  .Append(",\"is_default\":").Append(GetDefaultPrinter() == printer ? "true" : "false")
                  .Append(",\"online\":").Append(status.IsOnline ? "true" : "false")
                  .Append(",\"status\":").Append(WebServer.JsonEncode(status.StatusText))
                  .Append(",\"port\":").Append(WebServer.JsonEncode(status.PortName))
                  .Append(",\"driver\":").Append(WebServer.JsonEncode(status.DriverName))
                  .Append('}');
            }
            sb.Append(']');
            return sb.ToString();
        }

        /// <summary>打印机状态信息。</summary>
        public sealed class PrinterInfo
        {
            public bool IsOnline = true;
            public string StatusText = "就绪";
            public string PortName = "";
            public string DriverName = "";
        }

        /// <summary>
        /// 查询打印机状态（PRINTER_INFO_2）：Status 位标志 + pPortName + pDriverName。
        /// 使用 StructLayout 完整结构体，由 CLR 自动布局，避免手算偏移出错。
        /// </summary>
        public static PrinterInfo GetPrinterInfo(string printerName)
        {
            var info = new PrinterInfo();
            try
            {
                IntPtr hPrinter;
                if (!OpenPrinter(printerName, out hPrinter, IntPtr.Zero)) return info;
                try
                {
                    int needed;
                    GetPrinter(hPrinter, 2, IntPtr.Zero, 0, out needed);
                    if (needed <= 0) return info;
                    IntPtr buf = Marshal.AllocHGlobal(needed);
                    try
                    {
                        int got;
                        if (!GetPrinter(hPrinter, 2, buf, needed, out got)) return info;

                        var info2 = (PRINTER_INFO_2)Marshal.PtrToStructure(buf, typeof(PRINTER_INFO_2));
                        info.PortName = info2.pPortName ?? "";
                        info.DriverName = info2.pDriverName ?? "";
                        uint status = info2.Status;

                        // 状态标志 (PRINTER_STATUS_*)
                        if (status == 0) { info.IsOnline = true; info.StatusText = "就绪"; }
                        else
                        {
                            info.IsOnline = (status & 0x20 /* PRINTER_STATUS_OFFLINE */) == 0;
                            var sb = new StringBuilder();
                            if ((status & 0x1) != 0) sb.Append("暂停 ");
                            if ((status & 0x2) != 0) sb.Append("错误 ");
                            if ((status & 0x8) != 0) sb.Append("缺纸 ");
                            if ((status & 0x10) != 0) sb.Append("忙 ");
                            if ((status & 0x20) != 0) sb.Append("脱机 ");
                            if ((status & 0x40) != 0) sb.Append("纸用完 ");
                            if ((status & 0x80) != 0) sb.Append("输出被阻止 ");
                            if ((status & 0x100) != 0) sb.Append("IO激活 ");
                            if ((status & 0x200) != 0) sb.Append("需要人工干预 ");
                            if ((status & 0x800) != 0) sb.Append("重启 ");
                            if (sb.Length == 0) sb.Append($"状态码 {status} ");
                            info.StatusText = sb.ToString().Trim();
                        }
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
                finally { ClosePrinter(hPrinter); }
            }
            catch { /* 状态查询失败保持默认 */ }
            return info;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PRINTER_INFO_2
        {
            [MarshalAs(UnmanagedType.LPWStr)] public string pServerName;
            [MarshalAs(UnmanagedType.LPWStr)] public string pPrinterName;
            [MarshalAs(UnmanagedType.LPWStr)] public string pShareName;
            [MarshalAs(UnmanagedType.LPWStr)] public string pPortName;
            [MarshalAs(UnmanagedType.LPWStr)] public string pDriverName;
            [MarshalAs(UnmanagedType.LPWStr)] public string pComment;
            [MarshalAs(UnmanagedType.LPWStr)] public string pLocation;
            public IntPtr pDevMode;
            [MarshalAs(UnmanagedType.LPWStr)] public string pSepFile;
            [MarshalAs(UnmanagedType.LPWStr)] public string pPrintProcessor;
            [MarshalAs(UnmanagedType.LPWStr)] public string pDatatype;
            [MarshalAs(UnmanagedType.LPWStr)] public string pParameters;
            public IntPtr pSecurityDescriptor;
            public uint Attributes;
            public uint Priority;
            public uint DefaultPriority;
            public uint StartTime;
            public uint UntilTime;
            public uint Status;
            public uint cJobs;
            public uint AveragePPM;
        }

        /// <summary>获取默认打印机名。</summary>
        public static string GetDefaultPrinter()
        {
            try { return new PrinterSettings().PrinterName; }
            catch { return ""; }
        }

        /// <summary>判断是否为虚拟打印机。</summary>
        private static bool IsVirtualPrinter(string name)
        {
            var lower = name.ToLowerInvariant();
            if (lower.Contains("pdf") || lower.Contains("fax") || lower.Contains("xps") ||
                lower.Contains("onenote") || lower.Contains("virtual") || lower.Contains("microsoft print to") ||
                lower.Contains("snagit") || lower.Contains("send to"))
                return true;
            return false;
        }

        /// <summary>
        /// 打印文件。支持:
        /// - 图片: jpg/png/bmp/gif  ->  GDI 打印
        /// - 文本: txt              ->  GDI 打印
        /// - 其他 (pdf/doc/xls/ppt): 尝试 Shell 打开打印（需要安装对应软件）
        /// </summary>
        public static bool PrintFile(string path, string printer, int copies, int duplex,
            string papersize, string quality, string jobName = null)
        {
            // 设备互斥：等待扫描完成再打印（防止一体机设备冲突）
            if (!DeviceMutex.TryEnterPrint())
            {
                LastError = "设备忙：扫描正在进行中，请稍后重试";
                return false;
            }
            try
            {
                // 全局互斥：打印任务串行执行，防止多请求同时打印导致设备冲突/串台
                lock (PrintLock)
                {
                    LastError = "";
                    try
                    {
                        var ext = Path.GetExtension(path).ToLowerInvariant();

                        if (ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".bmp" || ext == ".gif")
                            return PrintImage(path, printer, copies, duplex, papersize, quality, jobName);

                        if (ext == ".txt")
                            return PrintText(path, printer, copies, duplex, papersize, jobName);

                        // PDF: 优先 poppler 渲染成图片再 GDI 打印（不依赖 Acrobat，老旧打印机兼容好）
                        if (ext == ".pdf")
                            return PrintPdf(path, printer, copies, duplex, papersize, quality, jobName);

                        // Office: 优先 COM 静默打印（可靠、无声无窗口），失败回退 Shell
                        if (ext == ".doc" || ext == ".docx" || ext == ".xls" || ext == ".xlsx" ||
                            ext == ".ppt" || ext == ".pptx")
                        {
                            if (OfficePrintHelper.TryPrint(path, printer, copies, ext))
                                return true;
                            Log.Warn($"COM 打印失败({OfficePrintHelper.LastError})，回退 Shell 打印: {Path.GetFileName(path)}");
                            return PrintViaShell(path, printer, jobName);
                        }

                        LastError = "不支持的文件类型: " + ext;
                        return false;
                    }
                    catch (Exception ex)
                    {
                        LastError = ex.Message;
                        return false;
                    }
                }
            }
            finally
            {
                DeviceMutex.ReleasePrint();
            }
        }

        /// <summary>
        /// GDI 打印图片。
        /// </summary>
        private static bool PrintImage(string path, string printer, int copies, int duplex,
            string papersize, string quality, string jobName)
        {
            using (var img = Image.FromFile(path))
            {
                var settings = new PrinterSettings { PrinterName = printer };
                if (!settings.IsValid)
                {
                    LastError = "打印机不可用: " + printer;
                    return false;
                }
                settings.Copies = (short)Math.Max(1, copies);

                var pd = new PageSettings(settings);
                if (papersize == "9") pd.PaperSize = new PaperSize("A4", 827, 1169); // A4
                else if (papersize == "8") pd.PaperSize = new PaperSize("A3", 1169, 1654);
                else if (papersize == "11") pd.PaperSize = new PaperSize("A5", 583, 827);
                else if (papersize == "1") pd.PaperSize = new PaperSize("Letter", 850, 1100);
                else if (papersize == "5") pd.PaperSize = new PaperSize("Legal", 850, 1400);

                var printDoc = new PrintDocument
                {
                    PrinterSettings = settings,
                    DefaultPageSettings = pd,
                    DocumentName = jobName ?? Path.GetFileName(path),
                };

                // 双面打印（Windows 驱动支持时）
                if (duplex == 2 || duplex == 16 || duplex == 5 || duplex == 6)
                    printDoc.PrinterSettings.Duplex = (Duplex)ConvertDuplex(duplex);

                printDoc.PrintPage += (s, e) =>
                {
                    var page = e.PageBounds;
                    var imageRatio = (double)img.Width / img.Height;
                    var pageRatio = (double)page.Width / page.Height;

                    int w, h;
                    if (imageRatio > pageRatio)
                    { w = page.Width; h = (int)(page.Width / imageRatio); }
                    else
                    { h = page.Height; w = (int)(page.Height * imageRatio); }

                    var x = page.X + (page.Width - w) / 2;
                    var y = page.Y + (page.Height - h) / 2;
                    e.Graphics.DrawImage(img, x, y, w, h);
                    e.HasMorePages = false;
                };

                printDoc.Print();
                return true;
            }
        }

        /// <summary>GDI 打印文本文件（自动适应页面，中文需字体）。</summary>
        private static bool PrintText(string path, string printer, int copies, int duplex,
            string papersize, string jobName)
        {
            string text;
            try { text = File.ReadAllText(path, Encoding.UTF8); }
            catch { text = File.ReadAllText(path, Encoding.Default); }

            var settings = new PrinterSettings { PrinterName = printer };
            if (!settings.IsValid) { LastError = "打印机不可用: " + printer; return false; }
            settings.Copies = (short)Math.Max(1, copies);
            if (duplex == 2) settings.Duplex = System.Drawing.Printing.Duplex.Vertical;

            var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            var printDoc = new PrintDocument
            {
                PrinterSettings = settings,
                DocumentName = jobName ?? Path.GetFileName(path),
            };

            int lineIdx = 0;
            float lineHeight = 0;

            printDoc.PrintPage += (s, e) =>
            {
                using (var font = new Font("宋体", 10.5f))
                {
                    lineHeight = font.GetHeight(e.Graphics);
                    float y = e.MarginBounds.Top;
                    float pageBottom = e.MarginBounds.Bottom;

                    while (lineIdx < lines.Length && y + lineHeight <= pageBottom)
                    {
                        e.Graphics.DrawString(lines[lineIdx] ?? "", font, Brushes.Black,
                            e.MarginBounds.Left, y);
                        y += lineHeight;
                        lineIdx++;
                    }
                }
                e.HasMorePages = lineIdx < lines.Length;
            };

            printDoc.Print();
            return true;
        }

        /// <summary>
        /// PDF 打印：用 poppler 的 pdftoppm 将每页渲染成 PNG，再逐页走 GDI 打印。
        /// 不依赖 Acrobat/Edge。pdftoppm 位置：exe 同目录 poppler_bin/，或系统 PATH。
        /// </summary>
        private static bool PrintPdf(string path, string printer, int copies, int duplex,
            string papersize, string quality, string jobName)
        {
            var settings = new PrinterSettings { PrinterName = printer };
            if (!settings.IsValid) { LastError = "打印机不可用: " + printer; return false; }

            // 1. 在临时目录渲染 PDF 为 PNG
            var tmpDir = Path.Combine(Path.GetTempPath(), "PrintServerPdf_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmpDir);
            var imagePaths = RenderPdfToImages(path, tmpDir);
            if (imagePaths == null || imagePaths.Count == 0)
            {
                LastError = "PDF 渲染失败: " + LastError;
                try { Directory.Delete(tmpDir, true); } catch { }
                return false;
            }

            try
            {
                // 2. 每个渲染页作为一页，顺序打印
                var printDoc = new PrintDocument
                {
                    PrinterSettings = settings,
                    DocumentName = jobName ?? Path.GetFileName(path),
                };
                if (duplex == 2 || duplex == 16) printDoc.PrinterSettings.Duplex = System.Drawing.Printing.Duplex.Vertical;
                else if (duplex == 3 || duplex == 5) printDoc.PrinterSettings.Duplex = System.Drawing.Printing.Duplex.Horizontal;

                var pd = new PageSettings(settings);
                if (papersize == "9") pd.PaperSize = new PaperSize("A4", 827, 1169);
                else if (papersize == "8") pd.PaperSize = new PaperSize("A3", 1169, 1654);
                else if (papersize == "11") pd.PaperSize = new PaperSize("A5", 583, 827);
                else if (papersize == "1") pd.PaperSize = new PaperSize("Letter", 850, 1100);
                else if (papersize == "5") pd.PaperSize = new PaperSize("Legal", 850, 1400);
                printDoc.DefaultPageSettings = pd;

                int pageIndex = 0;
                printDoc.PrintPage += (s, e) =>
                {
                    if (pageIndex >= imagePaths.Count)
                    {
                        e.HasMorePages = false;
                        return;
                    }
                    using (var img = System.Drawing.Image.FromFile(imagePaths[pageIndex]))
                    {
                        var bounds = e.PageBounds;
                        var ratio = (double)img.Width / img.Height;
                        var pageRatio = (double)bounds.Width / bounds.Height;

                        int w, h;
                        if (ratio > pageRatio) { w = bounds.Width; h = (int)(bounds.Width / ratio); }
                        else { h = bounds.Height; w = (int)(bounds.Height * ratio); }

                        e.Graphics.DrawImage(img, bounds.X + (bounds.Width - w) / 2,
                            bounds.Y + (bounds.Height - h) / 2, w, h);
                    }
                    pageIndex++;
                    e.HasMorePages = pageIndex < imagePaths.Count;
                };

                // 份数：PrintDocument 的 PrinterSettings.Copies 由打印驱动处理
                printDoc.PrinterSettings.Copies = (short)Math.Max(1, copies);
                printDoc.Print();
                Log.Info($"PDF 打印完成: {Path.GetFileName(path)} ({imagePaths.Count} 页, {printer})");
                return true;
            }
            catch (Exception ex)
            {
                LastError = "PDF 打印失败: " + ex.Message;
                return false;
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        /// <summary>用 pdftoppm 渲染 PDF 各页为 PNG，返回文件路径列表；失败返回 null。</summary>
        private static List<string> RenderPdfToImages(string pdfPath, string outDir)
        {
            var pdftoppm = FindPdftoppm();
            if (pdftoppm == null)
            {
                LastError = "未找到 pdftoppm (poppler)，无法打印 PDF";
                return null;
            }

            // 输出文件前缀: outDir/page-1.png, page-2.png...
            var prefix = Path.Combine(outDir, "page");
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = pdftoppm,
                Arguments = $"\"-png\" -r 200 \"{pdfPath}\" \"{prefix}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            try
            {
                using (var proc = System.Diagnostics.Process.Start(psi))
                {
                    if (proc == null) { LastError = "pdftoppm 启动失败"; return null; }
                    proc.WaitForExit(120000); // 2 分钟超时
                    if (!proc.HasExited)
                    {
                        try { proc.Kill(); } catch { }
                        LastError = "pdftoppm 超时";
                        return null;
                    }
                }

                var files = new List<string>();
                foreach (var f in Directory.GetFiles(outDir, "page-*.png"))
                    files.Add(f);
                files.Sort((a, b) => string.CompareOrdinal(a, b));
                if (files.Count == 0)
                {
                    LastError = "pdftoppm 未输出任何页面 (文件可能损坏或空白)";
                    return null;
                }
                return files;
            }
            catch (Exception ex)
            {
                LastError = "pdftoppm 执行失败: " + ex.Message;
                return null;
            }
        }

        /// <summary>查找 pdftoppm.exe：优先 Program.PopplerDir（单文件解压目录），其次系统 PATH。</summary>
        private static string FindPdftoppm()
        {
            var candidates = new List<string>();
            // 单文件/开发模式: Program.PopplerDir
            if (!string.IsNullOrEmpty(Program.PopplerDir))
                candidates.Add(Path.Combine(Program.PopplerDir, "pdftoppm.exe"));
            // exe 同目录
            candidates.Add(Path.Combine(Program.BaseDir, "poppler_bin", "pdftoppm.exe"));
            candidates.Add(Path.Combine(Program.BaseDir, "pdftoppm.exe"));
            // 上级目录常见部署位置
            var parent = Path.GetDirectoryName(Program.BaseDir.TrimEnd('\\', '/'));
            if (!string.IsNullOrEmpty(parent))
            {
                candidates.Add(Path.Combine(parent, "poppler_bin", "pdftoppm.exe"));
                candidates.Add(Path.Combine(parent, "poppler", "Library", "bin", "pdftoppm.exe"));
            }
            foreach (var c in candidates)
                if (File.Exists(c)) return c;
            return null; // 交给系统 PATH 由 Process.Start 解析（只传文件名时）
        }

        /// <summary>Office/PDF 文档：尝试 ShellExecute "print" 交给关联程序（Word/WPS/Acrobat 等）。</summary>
        private static bool PrintViaShell(string path, string printer, string jobName)
        {
            // 打印到默认打印机。若指定打印机非默认，Shell 打印默认使用系统默认打印机。
            var result = ShellExecute(IntPtr.Zero, "print", path, null, Path.GetDirectoryName(path), 1);
            if (result.ToInt64() <= 32)
            {
                LastError = "Shell 打印失败，错误码: " + result;
                return false;
            }
            Log.Info($"Shell 打印已发送: {Path.GetFileName(path)} -> {printer} (默认打印机)");
            return true;
        }

        private static int ConvertDuplex(int duplex)
        {
            // 1=单面 2=双面长边 3=双面短边(部分驱动)
            switch (duplex)
            {
                case 1: return 1; // Simplex
                case 2:
                case 16: return 2; // Vertical
                case 3:
                case 5: return 3;  // Horizontal
                default: return 2;
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr ShellExecute(IntPtr hwnd, string lpOperation, string lpFile,
            string lpParameters, string lpDirectory, int nShowCmd);
    }
}