using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PrintServer
{
    /// <summary>
    /// Office 文档 COM 自动化静默打印 (Word/Excel/PowerPoint)。
    /// 通过 ProgID 创建应用实例，不引用 Office PIA，兼容 MS Office 与部分 WPS。
    /// 打印前通过 winspool 查询打印机端口，设置 ActivePrinter 支持指定打印机。
    /// COM 调用在 STA 线程 + 超时控制内执行，避免卡死。
    /// </summary>
    internal static class OfficePrintHelper
    {
        public static string LastError = "";

        // ===== winspool P/Invoke（查询打印机端口）=====

        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool GetPrinter(IntPtr hPrinter, int Level, IntPtr pPrinter, int cbBuf, out int pcbNeeded);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool ClosePrinter(IntPtr hPrinter);

        // DocumentProperties: 获取/修改打印机的 DEVMODE（用于设置双面打印）
        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int DocumentProperties(IntPtr hWnd, IntPtr hPrinter,
            string pDeviceName, IntPtr pDevModeOutput, IntPtr pDevModeInput, int fMode);

        private const int DM_IN_BUFFER = 8;
        private const int DM_OUT_BUFFER = 2;
        private const int DM_DUPLEX = 0x1000;
        private const int DM_UPDATE = 1;

        // DEVMODE(Unicode) 固定字段偏移（x86/x64 一致）：
        // dmFields @72 (DWORD), dmDuplex @94 (WORD)
        private const int DEVMODE_DMFIELDS_OFFSET = 72;
        private const int DEVMODE_DMDUPLEX_OFFSET = 94;

        private const int DMDUP_SIMPLEX = 1;
        private const int DMDUP_VERTICAL = 2;
        private const int DMDUP_HORIZONTAL = 3;

        /// <summary>
        /// 通过 winspool DEVMODE 设置打印机双面模式（1=单面 2=长边 3=短边）。
        /// 返回 true 表示设置成功；失败返回 false（打印继续，用原设置）。
        /// COM 的 PrintOut 没有双面参数，这是唯一可靠途径。
        /// </summary>
        public static bool SetPrinterDuplex(string printerName, int duplex)
        {
            try
            {
                if (string.IsNullOrEmpty(printerName)) return false;

                int mode;
                switch (duplex)
                {
                    case 1: mode = DMDUP_SIMPLEX; break;     // 单面
                    case 2: mode = DMDUP_VERTICAL; break;    // 长边
                    case 3: mode = DMDUP_HORIZONTAL; break;  // 短边
                    default: return false;
                }

                IntPtr hPrinter;
                if (!OpenPrinter(printerName, out hPrinter, IntPtr.Zero)) return false;
                try
                {
                    // 第一次调用：获取 DEVMODE 缓冲区大小
                    int size = DocumentProperties(IntPtr.Zero, hPrinter, printerName,
                        IntPtr.Zero, IntPtr.Zero, 0);
                    if (size <= 0) return false;

                    IntPtr devMode = Marshal.AllocHGlobal(size);
                    try
                    {
                        // 第二次调用：取当前 DEVMODE 内容
                        if (DocumentProperties(IntPtr.Zero, hPrinter, printerName,
                            devMode, IntPtr.Zero, DM_OUT_BUFFER) < 0)
                            return false;

                        // 置 dmFields 的 DM_DUPLEX 标志，并写入 dmDuplex
                        int fields = Marshal.ReadInt32(devMode, DEVMODE_DMFIELDS_OFFSET);
                        Marshal.WriteInt32(devMode, DEVMODE_DMFIELDS_OFFSET, fields | DM_DUPLEX);
                        Marshal.WriteInt16(devMode, DEVMODE_DMDUPLEX_OFFSET, (short)mode);

                        // 提交修改到打印机（DM_UPDATE 使设置生效）
                        int result = DocumentProperties(IntPtr.Zero, hPrinter, printerName,
                            devMode, devMode, DM_IN_BUFFER | DM_OUT_BUFFER | DM_UPDATE);
                        return result >= 0;
                    }
                    finally { Marshal.FreeHGlobal(devMode); }
                }
                finally { ClosePrinter(hPrinter); }
            }
            catch { return false; }
        }

        /// <summary>打印结束后恢复打印机双面设置为单面。</summary>
        public static void ResetPrinterDuplex(string printerName)
        {
            try { SetPrinterDuplex(printerName, DMDUP_SIMPLEX); } catch { }
        }

        /// <summary>
        /// 查询打印机端口名（PRINTER_INFO_5.pPortName）。
        /// 结构(x64): pPrinterName @0, pPortName @8, Attributes @16 ...
        /// </summary>
        private static string GetPrinterPort(string printerName)
        {
            if (string.IsNullOrEmpty(printerName)) return null;
            IntPtr hPrinter;
            if (!OpenPrinter(printerName, out hPrinter, IntPtr.Zero)) return null;
            try
            {
                int needed;
                GetPrinter(hPrinter, 5, IntPtr.Zero, 0, out needed);
                if (needed <= 0) return null;
                IntPtr buf = Marshal.AllocHGlobal(needed);
                try
                {
                    int got;
                    if (!GetPrinter(hPrinter, 5, buf, needed, out got)) return null;
                    // PRINTER_INFO_5: pPrinterName = *(IntPtr*)buf, pPortName = *(IntPtr*)(buf + IntPtr.Size)
                    IntPtr portPtr = Marshal.ReadIntPtr(buf, IntPtr.Size);
                    return portPtr == IntPtr.Zero ? null : Marshal.PtrToStringUni(portPtr);
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            finally { ClosePrinter(hPrinter); }
        }

        /// <summary>
        /// 构造 Office ActivePrinter 字符串: "打印机名 on 端口:"。
        /// 查不到端口返回 null（调用方保持默认打印机）。
        /// </summary>
        private static string BuildActivePrinter(string printerName)
        {
            var port = GetPrinterPort(printerName);
            if (string.IsNullOrEmpty(port)) return null;
            return printerName + " on " + port + ":";
        }

        /// <summary>尝试在 Office 应用上设置指定打印机；失败静默（用默认）。</summary>
        private static void ApplyActivePrinter(dynamic app, string printerName)
        {
            try
            {
                var ap = BuildActivePrinter(printerName);
                if (!string.IsNullOrEmpty(ap))
                {
                    app.ActivePrinter = ap;
                    Log.Info($"已设置打印到: {ap}");
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"设置 ActivePrinter 失败(将用默认打印机): {ex.Message}");
            }
        }

        /// <summary>按扩展名分发 COM 打印；成功返回 true，失败返回 false（调用方决定是否 fallback）。</summary>
        public static bool TryPrint(string path, string printer, int copies, int duplex, string papersize, string ext)
        {
            LastError = "";
            if (ext == ".doc" || ext == ".docx") return WordPrint(path, printer, copies, duplex, papersize);
            if (ext == ".xls" || ext == ".xlsx") return ExcelPrint(path, printer, copies, duplex, papersize);
            if (ext == ".ppt" || ext == ".pptx") return PptPrint(path, printer, copies, duplex);
            return false;
        }

        /// <summary>在 STA 线程执行 Func，超过 timeoutMs 视为失败（COM 卡死保护）。</summary>
        private static T RunInSta<T>(Func<T> action, int timeoutMs)
        {
            T result = default(T);
            Exception captured = null;
            var done = new ManualResetEvent(false);

            var t = new Thread(() =>
            {
                try { result = action(); }
                catch (Exception ex) { captured = ex; }
                finally { done.Set(); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();

            if (!done.WaitOne(timeoutMs))
            {
                LastError = "COM 调用超时（Office 应用无响应）";
                return default(T);
            }
            if (captured != null)
            {
                LastError = captured.Message;
                return default(T);
            }
            return result;
        }

        /// <summary>Word: Documents.Open → 设 ActivePrinter → 设纸张/双面 → PrintOut → Close → Quit。</summary>
        private static bool WordPrint(string path, string printer, int copies, int duplex, string papersize)
        {
            return RunInSta(() =>
            {
                dynamic word = null, doc = null;
                try
                {
                    Type t = Type.GetTypeFromProgID("Word.Application");
                    if (t == null) { LastError = "未安装 Word/WPS"; return false; }
                    word = Activator.CreateInstance(t);
                    word.Visible = false;
                    try { word.DisplayAlerts = 0; } catch { }
                    ApplyActivePrinter(word, printer);

                    doc = word.Documents.Open(path, false, true); // ReadOnly
                    try
                    {
                        // 纸张大小（Word PageSetup.PaperSize: 1=Letter 5=Legal 7=A4 8=A3 11=A5）
                        try
                        {
                            if (papersize == "9") doc.PageSetup.PaperSize = 7;       // A4
                            else if (papersize == "8") doc.PageSetup.PaperSize = 8;  // A3
                            else if (papersize == "11") doc.PageSetup.PaperSize = 11; // A5
                            else if (papersize == "1") doc.PageSetup.PaperSize = 1;  // Letter
                            else if (papersize == "5") doc.PageSetup.PaperSize = 5;  // Legal
                        }
                        catch (Exception ex) { Log.Warn($"Word 设置纸张失败: {ex.Message}"); }

                        // 双面：COM PrintOut 无双面参数，通过 DEVMODE 设置打印机双面模式
                        bool duplexSet = false;
                        try { duplexSet = SetPrinterDuplex(printer, duplex); } catch { }

                        try
                        {
                            // PrintOut(Background, Append, Range, OutputFileName, From, To,
                            //           Item, Copies, Pages, PageType, PrintToFile, Collate,
                            //           ...)
                            doc.PrintOut(false, false, 0, "", "", "", 0, copies, "", 0, false, true);
                        }
                        finally
                        {
                            if (duplexSet) try { ResetPrinterDuplex(printer); } catch { }
                        }
                    }
                    finally
                    {
                        try { doc.Close(false); } catch { }
                    }
                    try { word.Quit(); } catch { }
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = "Word 打印失败: " + ex.Message;
                    return false;
                }
                finally
                {
                    try { if (doc != null) Marshal.ReleaseComObject(doc); } catch { }
                    try { if (word != null) Marshal.ReleaseComObject(word); } catch { }
                }
            }, 90000);
        }

        /// <summary>Excel: Workbooks.Open(ReadOnly) → 设 ActivePrinter → 设纸张/双面 → PrintOut → Close → Quit。</summary>
        private static bool ExcelPrint(string path, string printer, int copies, int duplex, string papersize)
        {
            return RunInSta(() =>
            {
                dynamic excel = null, wb = null;
                try
                {
                    Type t = Type.GetTypeFromProgID("Excel.Application");
                    if (t == null) { LastError = "未安装 Excel/WPS"; return false; }
                    excel = Activator.CreateInstance(t);
                    excel.Visible = false;
                    try { excel.DisplayAlerts = false; } catch { }
                    ApplyActivePrinter(excel, printer);

                    wb = excel.Workbooks.Open(path, 0, true); // ReadOnly
                    try
                    {
                        // 纸张大小（Excel PageSetup.PaperSize: xlPaperLetter=1 xlPaperLegal=5
                        // xlPaperA4=9 xlPaperA3=8 xlPaperA5=11）
                        try
                        {
                            dynamic ps = wb.ActiveSheet.PageSetup;
                            if (papersize == "9") ps.PaperSize = 9;        // A4
                            else if (papersize == "8") ps.PaperSize = 8;   // A3
                            else if (papersize == "11") ps.PaperSize = 11; // A5
                            else if (papersize == "1") ps.PaperSize = 1;   // Letter
                            else if (papersize == "5") ps.PaperSize = 5;   // Legal
                        }
                        catch (Exception ex) { Log.Warn($"Excel 设置纸张失败: {ex.Message}"); }

                        // 双面：DEVMODE 设置打印机双面模式
                        bool duplexSet = false;
                        try { duplexSet = SetPrinterDuplex(printer, duplex); } catch { }

                        try
                        {
                            // Workbook.PrintOut(From, To, Copies, Preview, ActivePrinter, PrintToFile, ...)
                            wb.PrintOut(1, 0, copies, false, "", false);
                        }
                        finally
                        {
                            if (duplexSet) try { ResetPrinterDuplex(printer); } catch { }
                        }
                    }
                    finally
                    {
                        try { wb.Close(false); } catch { }
                    }
                    try { excel.Quit(); } catch { }
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = "Excel 打印失败: " + ex.Message;
                    return false;
                }
                finally
                {
                    try { if (wb != null) Marshal.ReleaseComObject(wb); } catch { }
                    try { if (excel != null) Marshal.ReleaseComObject(excel); } catch { }
                }
            }, 90000);
        }

        /// <summary>PowerPoint: Presentations.Open → 设 ActivePrinter → 设双面(DEVMODE) → PrintOut → Close → Quit。
        /// PPT 无页面纸张概念，纸张大小由驱动按幻灯片尺寸处理，仅设置双面。</summary>
        private static bool PptPrint(string path, string printer, int copies, int duplex)
        {
            return RunInSta(() =>
            {
                dynamic ppt = null, pres = null;
                try
                {
                    Type t = Type.GetTypeFromProgID("PowerPoint.Application");
                    if (t == null) { LastError = "未安装 PowerPoint/WPS"; return false; }
                    ppt = Activator.CreateInstance(t);
                    ApplyActivePrinter(ppt, printer);

                    // Presentation.Open(FileName, ReadOnly, Untitled, WithWindow)
                    pres = ppt.Presentations.Open(path, -1 /*msoTrue*/, 0 /*msoFalse*/, 0 /*msoFalse*/);
                    try
                    {
                        // 双面：DEVMODE 设置打印机双面模式
                        bool duplexSet = false;
                        try { duplexSet = SetPrinterDuplex(printer, duplex); } catch { }

                        try
                        {
                            // PrintOut(From, To, Copies, Collate, PrintToFile)
                            pres.PrintOut(1, 0, copies, 1, "");
                        }
                        finally
                        {
                            if (duplexSet) try { ResetPrinterDuplex(printer); } catch { }
                        }
                    }
                    finally
                    {
                        try { pres.Close(); } catch { }
                    }
                    try { ppt.Quit(); } catch { }
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = "PowerPoint 打印失败: " + ex.Message;
                    return false;
                }
                finally
                {
                    try { if (pres != null) Marshal.ReleaseComObject(pres); } catch { }
                    try { if (ppt != null) Marshal.ReleaseComObject(ppt); } catch { }
                }
            }, 120000);
        }
    }
}