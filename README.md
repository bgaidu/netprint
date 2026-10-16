# netprint — 内网打印及扫描服务 (C# 原生版)

基于 C# (.NET Framework 4.8) 重写的内网打印/扫描服务，**单文件发布**（exe 自包含前端/poppler/图标）。
支持 Windows 7 SP1 / 8 / 10 / 11 (x64)。

当前版本：**v0.0.0.6**（[Releases](https://github.com/bgaidu/netprint/releases) 下载）

## 功能
- **打印**: 图片(JPG/PNG/BMP/GIF)、文本(TXT) GDI 直打；PDF 走 poppler 渲染成图再打（不依赖 Acrobat）；Word/Excel/PPT 走 COM 静默打印（指定打印机、份数），失败自动回退 Shell
- **扫描**: WIA 枚举 + 扫描（DPI/格式可选），PNG/JPEG 输出；设备被锁定自动释放（杀残留进程+重启 WIA 服务+重试）
- **Web 界面**: 浏览器访问 http://IP:端口 ，打印/扫描/文件管理/日志 四页签
- **网页密码**: 可选启用访问密码（托盘设置/修改/关闭），启用后需登录才能打印/扫描/看日志
- **日志**: 按天生成 `print_log_YYYYMMDD.txt`，记录访问 IP、打印/扫描内容、密码拒绝等；网页可查看任意一天，保留 7 天自动清理
- **托盘**: 打开网页、开机自启、更改端口、网页密码、网络配置(静态IP/DHCP)、查看任意天日志、清理日志、重启、退出、关于
- **文件管理**: 上传自动保存，预览/下载/删除，10分钟自动清理
- **可靠性**: 健康看门狗(15秒检测/自动重启服务)、打印互斥锁(防串台)、文件名净化、路径穿越防护、类型白名单、上传大小限制(100MB)
- **单文件发布**: 前端/poppler/图标全部嵌入 exe，启动时 poppler 解压到临时目录，部署只需一个 exe

## 部署
1. 目标机安装 .NET Framework 4.8 (Windows 10/11 自带，Win7 SP1 需安装一次)
2. 从 [Releases](https://github.com/bgaidu/netprint/releases) 下载 `PrintServer.exe`（单文件），放到英文路径目录
3. 双击运行（自动请求管理员权限提权；拒绝则以 localhost 模式运行）
4. 托盘图标右键 → 打开网页，或局域网设备浏览器访问 `http://电脑IP:5000`

## 自动构建 (CI)
- 推送 `main` 自动编译（GitHub Actions）
- 打 tag `v*`（如 `git tag v0.0.0.3 && git push origin v0.0.0.3`）自动构建并发布 Release

## 开发
- VS2022 或 dotnet SDK 9
- `dotnet build PrintServer.csproj -c Release`
- 修改端口: `PrintServer.exe --port=8080` 或托盘"更改端口"

## 目录结构
```
PrintServer.csproj    项目文件 (net48, 单文件发布)
Program.cs            入口：提权/路径/poppler解压/定时器
WebServer.cs          HttpListener 自托管 + 路由 + 密码校验 + 访问日志
ApiRouter.cs          API 路由 + multipart 解析
PrinterService.cs     打印核心 (GDI/PDF/分发/互斥锁)
OfficePrintHelper.cs  Office COM 打印 (Word/Excel/PPT)
ScannerService.cs     WIA 扫描 + 防锁释放
FileStore.cs          文件存储/列表/清理
ResourceManager.cs    嵌入资源读取 (单文件发布)
Settings.cs           配置 (网页密码, config.json)
Log.cs                按天日志
HealthWatchdog.cs     健康自愈
NetworkConfig.cs      网络配置 (静态IP/DHCP)
AutoStart.cs          开机自启 (注册表)
TrayIcon.cs           托盘菜单
PasswordDialog.cs     密码设置对话框
AboutDialog.cs        关于对话框
wwwroot/              前端 (Bootstrap 复用, 嵌入exe)
tools/poppler_bin/    poppler (PDF 渲染, 嵌入exe)
.github/workflows/    CI 自动构建 + Release
```

## 已知限制
- Office COM 打印需本机装有 Word/WPS 或对应程序
- WIA 扫描仅支持 WIA 兼容设备（与原版一致）
- 非管理员运行仅可 localhost 访问；局域网共享需管理员（自动提示提权）

## 作者
Bgaidu & AI