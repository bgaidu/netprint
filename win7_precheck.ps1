<#
.SYNOPSIS
    PrintServer (net48) Win7 deployment precheck script.
.DESCRIPTION
    Checks prerequisites for running PrintServer.exe on Windows 7 and prints advice.
    Read-only: does not modify the system.
.NOTES
    Target: PrintServer.exe (target framework: .NET Framework 4.8)
#>

$ErrorActionPreference = 'SilentlyContinue'

function Write-Result($name, $ok, $detail, $fix) {
    $tag = if ($ok) { '  [OK]   ' } else { '  [MISS]  ' }
    Write-Host "$tag$name"
    if ($detail) { Write-Host "          $detail" }
    if (-not $ok -and $fix) { Write-Host "          -> Fix: $fix" }
    Write-Host ""
}

Write-Host "========================================================"
Write-Host " PrintServer Win7 Precheck"
Write-Host "========================================================"
Write-Host ""

# 1) OS and Service Pack
$os = Get-CimInstance Win32_OperatingSystem
$prodName = (Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion" -ErrorAction SilentlyContinue).ProductName
if (-not $prodName) { $prodName = $os.Caption }
$ver = $os.Version
$win7 = ($ver -and $ver.StartsWith("6.1")) -or ($prodName -and $prodName -match "Windows 7")
$sp = $os.ServicePackMajorVersion
Write-Result "Windows version" $win7 "Detected: $prodName (Build $ver, SP $sp)" `
    "Require Windows 7 SP1+. If SP=0, install Windows 7 Service Pack 1 first."
$sp1 = ($sp -ge 1)
Write-Result "Service Pack 1" $sp1 "Current SP major: $sp" `
    "Install Windows 7 SP1 (KB976932)."

# 2) Admin rights
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Write-Result "Admin rights (for this script)" $isAdmin "Current: $(if($isAdmin){'Admin'}else{'Standard user'})" `
    "Run PrintServer.exe as Administrator (right-click -> Run as admin); otherwise firewall port stays closed and LAN devices cannot reach it."

# 3) .NET Framework version
$netKey = Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" -ErrorAction SilentlyContinue
$netVer = if ($netKey) { $netKey.Release } else { 0 }
function ReleaseToVer($r) {
    if ($r -ge 533320) { return "4.8.1+" }
    if ($r -ge 528040) { return "4.8" }
    if ($r -ge 461808) { return "4.7.2" }
    if ($r -ge 460798) { return "4.6.2" }
    if ($r -ge 0) { return "below 4.6.2" }
}
$netVerStr = ReleaseToVer $netVer
$netOk = $netVer -ge 460798
Write-Result ".NET Framework" $netOk "Detected Release=$netVer ($netVerStr)" `
    "Install .NET Framework 4.8 offline runtime (ndp48-x86-x64-allos.exe)."

# 4) Visual C++ runtime (poppler pdftoppm.exe depends on it)
$vcKeys = @(
    "HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64",
    "HKLM:\SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x64"
)
$vcFound = $false; $vcVer = ""
foreach ($k in $vcKeys) {
    $p = Get-ItemProperty $k -ErrorAction SilentlyContinue
    if ($p -and $p.Version) { $vcFound = $true; $vcVer = $p.Version; break }
}
if (-not $vcFound) {
    $vcFound = Get-CimInstance Win32_Product | Where-Object { $_.Name -match "Visual C\+\+ 20(15|17|19|22)" } | Select-Object -First 1
    if ($vcFound) { $vcVer = $vcFound.Version }
}
Write-Result "Visual C++ runtime (x64)" ([bool]$vcFound) "$(if($vcFound){"Installed: $vcVer"}else{'Not found'})" `
    "Install Visual C++ Redistributable 2015-2022 x64 (vc_redist.x64.exe). Required by PDF printing."

# 5) Office / WPS (only needed for Word/Excel/PPT printing)
$haveOffice = $false; $officeName = ""
foreach ($progId in @("Word.Application","Excel.Application","PowerPoint.Application")) {
    $t = $null
    try { $t = [Type]::GetTypeFromProgID($progId) } catch {}
    if ($t) { $haveOffice = $true; $officeName = $progId; break }
}
Write-Result "Office / WPS (for Office docs)" $haveOffice "$(if($haveOffice){"Installed: $officeName"}else{'Not installed'})" `
    "Printing Word/Excel/PPT needs Office or WPS on the target machine. Image/PDF/Text printing does NOT need Office."

# 6) Default port availability (default 5000, override with --port)
$port = 5000
$inUse = $false
try {
    $used = Get-NetTCPConnection -LocalPort $port -ErrorAction SilentlyContinue
    if ($used) { $inUse = $true }
} catch { }
Write-Result "Default port $port availability" (-not $inUse) "$(if($inUse){'Port occupied'}else{'Port free'})" `
    "If occupied, run with --port=xxxx and open that port in firewall accordingly."

Write-Host "========================================================"
Write-Host " Summary"
Write-Host "========================================================"
$allOk = $sp1 -and $netOk -and $vcFound
if ($allOk) {
    Write-Host "  Core prerequisites (SP1 + .NET + VC++) satisfied. You can run PrintServer.exe."
    Write-Host "  Run exe as Administrator; Office/WPS only needed for Office documents."
} else {
    Write-Host "  Some items are missing (see [MISS] above). Fix them before running."
}
Write-Host ""
Write-Host "  Run: right-click PrintServer.exe -> Run as administrator"
Write-Host "  (Without admin, only localhost works and firewall port stays closed)"
Write-Host "========================================================"

if (-not $psISE) {
    Write-Host ""
    Write-Host "Press any key to exit..."
    $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
}
