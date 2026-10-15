# netprint — 内网打印及扫描服务 (C# 原生版)

基于 C# (.NET Framework 4.8) 重写的内网打印/扫描服务，后端零依赖、单文件夹部署。
支持 Windows 7 SP1 / 8 / 10 / 11 (x64)。

## 功能
- **打印**: 图片(JPG/PNG/BMP/GIF)、文本(TXT) GDI 直打；PDF 走 poppler 渲染成图再打（不依赖 Acrobat）；Word/Excel/PPT 走 COM 静默打印（指定打印机、份数），失败自动回退 Shell
- **扫描**: WIA 枚举 + 扫描（DPI/格式可选），PNG/JPEG 输出
- **Web 界面**: 浏览器访问 http://IP:端口 ，打印/扫描/文件管理三页签
- **托盘**: 打开网页、开机自启、更改端口、查看/清理日志、重启、退出
- **文件管理**: 上传自动保存，预览/下载/删除，10分钟自动清理
- **安全性**: 文件名净化、路径穿越防护、类型白名单、上传大小限制、JSON 正确编码

## 部署
1. 目标机安装 .NET Framework 4.8 (Windows 10/11 自带，Win7 SP1 需安装一次)
2. 将整个 `bin\Release\net48\` 目录拷到目标机（路径建议纯英文）
3. 右键 `PrintServer.exe` → 以管理员身份运行（或首次运行自动提权）
4. 托盘图标右键 → 打开网页，或局域网设备浏览器访问 `http://电脑IP:5000`

## 开发
- VS2022 或 dotnet SDK
- `dotnet build PrintServer.csproj -c Release`
- 修改端口: `PrintServer.exe --port=8080` 或托盘"更改端口"

## 目录结构
```
PrintServer.csproj  项目文件 (net48, WinForms)
Program.cs          入口：提权/路径/清理定时器
WebServer.cs        HttpListener 自托管 + 路由
ApiRouter.cs        API 路由 + multipart 解析
PrinterService.cs   打印核心 (GDI/PDF/分发/互斥锁)
OfficePrintHelper.cs Office COM 打印 (Word/Excel/PPT)
ScannerService.cs   WIA 扫描
FileStore.cs        文件存储/列表/清理
AutoStart.cs        开机自启 (注册表)
TrayIcon.cs         托盘菜单
Log.cs              日志
wwwroot/            前端 (Bootstrap 复用)
tools/poppler_bin/  poppler (PDF 渲染)
```

## 已知限制
- Office COM 打印需本机装有 Word/WPS 或对应程序
- WIA 扫描仅支持 WIA 兼容设备（与原版一致）
- 非管理员运行仅可 localhost 访问；局域网共享需管理员（自动提示提权）