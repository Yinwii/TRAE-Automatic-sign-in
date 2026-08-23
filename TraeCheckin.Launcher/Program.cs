using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace TraeCheckin.Launcher;

internal static class Program
{
    private const string MainAppExe = "TraeCheckin.exe";
    private const string DotNetDownloadPage = "https://dotnet.microsoft.com/download/dotnet/9.0";
    private const string WebView2DownloadUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
    private const string WebView2RuntimeGuid = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);

    private const uint MB_OK = 0x0;
    private const uint MB_YESNO = 0x4;
    private const uint MB_ICONINFO = 0x40;
    private const uint MB_ICONWARN = 0x30;
    private const int IDYES = 6;

    private static int Main()
    {
        try
        {
            // 1. 检测 .NET 9 桌面运行时，缺失则自动安装
            if (!HasDotNet9DesktopRuntime())
            {
                ShowInfo(
                    "检测到系统中缺少 .NET 9 桌面运行时。\n\n" +
                    "接下来将自动下载并安装。安装过程中如弹出「用户账户控制(UAC)」窗口，请点击「是」。\n" +
                    "安装可能需要几分钟，请耐心等待。");

                if (!TryInstallDotNet9DesktopRuntime() || !HasDotNet9DesktopRuntime())
                {
                    ShowError(
                        "自动安装 .NET 9 桌面运行时失败。\n\n" +
                        "请手动下载并安装后重试：\n" + DotNetDownloadPage);
                    return 1;
                }
            }

            // 2. 检测 WebView2 运行时，缺失则弹窗引导安装
            if (!HasWebView2Runtime())
            {
                int choice = MessageBoxW(
                    IntPtr.Zero,
                    "检测到系统中缺少 Microsoft Edge WebView2 运行时。\n\n" +
                    "该组件用于程序内登录窗口的内嵌浏览器。\n\n" +
                    "是否现在打开微软官方下载页面进行安装？",
                    "缺少 WebView2 运行时",
                    MB_YESNO | MB_ICONWARN);

                if (choice == IDYES)
                {
                    OpenBrowser(WebView2DownloadUrl);
                }
            }

            // 3. 启动主程序
            return LaunchMainApp() ? 0 : 1;
        }
        catch (Exception ex)
        {
            ShowError("启动器运行时出错：" + ex.Message);
            return 1;
        }
    }

    /// <summary>检测是否安装了 .NET 9 Windows Desktop 运行时（x64）。</summary>
    private static bool HasDotNet9DesktopRuntime()
    {
        // 以文件系统为准：.NET 运行时默认安装在 <dotnet 根>\shared\Microsoft.WindowsDesktop.App\9.x.x\
        // 比注册表更可靠（dotnet --list-runtimes 所列版本均在此目录下）。
        foreach (string root in GetDotNetRootCandidates())
        {
            string dir = Path.Combine(root, "shared", "Microsoft.WindowsDesktop.App");
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (string sub in Directory.EnumerateDirectories(dir))
            {
                string name = Path.GetFileName(sub);
                if (name.StartsWith("9.", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>枚举可能的 .NET 安装根目录候选。</summary>
    private static IEnumerable<string> GetDotNetRootCandidates()
    {
        // 标准安装目录（64 位）
        string? pf64 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(pf64))
        {
            yield return Path.Combine(pf64, "dotnet");
        }

        // 32 位安装目录（兼容性兜底）
        string? pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
        if (!string.IsNullOrEmpty(pf86))
        {
            yield return Path.Combine(pf86, "dotnet");
        }

        // 用户自定义 DOTNET_ROOT
        string? dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(dotnetRoot))
        {
            yield return dotnetRoot;
        }
    }

    /// <summary>检测是否安装了 WebView2 Evergreen 运行时。</summary>
    private static bool HasWebView2Runtime()
    {
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using RegistryKey? key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\EdgeUpdate\Clients\" + WebView2RuntimeGuid);
            if (key is not null && !string.IsNullOrEmpty(key.GetValue("pv") as string))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>尝试通过 winget 自动安装 .NET 9 桌面运行时。</summary>
    private static bool TryInstallDotNet9DesktopRuntime()
    {
        if (!WingetAvailable())
        {
            ShowError(
                "系统中未找到 Windows 包管理器(winget)。\n\n" +
                "将打开微软官方下载页面，请手动下载并安装 .NET 9 桌面运行时。");
            OpenBrowser(DotNetDownloadPage);
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "winget",
                Arguments = "install --id Microsoft.DotNet.DesktopRuntime.9 --exact --silent --accept-package-agreements --accept-source-agreements",
                UseShellExecute = true,
                Verb = "runas",
            };

            using var process = Process.Start(psi);
            process?.WaitForExit();
            return true;
        }
        catch (Exception ex)
        {
            ShowError("自动安装失败：" + ex.Message + "\n\n请手动下载安装：\n" + DotNetDownloadPage);
            OpenBrowser(DotNetDownloadPage);
            return false;
        }
    }

    /// <summary>检测 winget（Windows 包管理器）是否可用。</summary>
    private static bool WingetAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "winget",
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* 忽略 */ }
                return false;
            }

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>启动同目录下的主程序 TraeCheckin.exe。</summary>
    private static bool LaunchMainApp()
    {
        string exe = Path.Combine(AppContext.BaseDirectory, MainAppExe);
        if (!File.Exists(exe))
        {
            ShowError("未找到主程序文件：\n" + exe);
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                WorkingDirectory = AppContext.BaseDirectory,
            });
            return true;
        }
        catch (Exception ex)
        {
            ShowError("启动主程序失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>用默认浏览器打开网页。</summary>
    private static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // 打开浏览器失败时忽略，用户可手动复制链接。
        }
    }

    private static void ShowInfo(string text)
    {
        MessageBoxW(IntPtr.Zero, text, "TraeCheckin 启动器", MB_OK | MB_ICONINFO);
    }

    private static void ShowError(string text)
    {
        MessageBoxW(IntPtr.Zero, text, "TraeCheckin 启动器", MB_OK | MB_ICONWARN);
    }
}
