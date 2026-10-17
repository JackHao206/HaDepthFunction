using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace DepthFusionApp
{
    /// <summary>
    /// 配置 Halcon 原生运行时（halcon.dll 等）的搜索目录。
    /// halcondotnet.dll 是托管包装，真正的算子在 %HALCONROOT%\bin\x64-win64 下的原生 DLL 中。
    /// </summary>
    internal static class HalconRuntime
    {
        private const string FallbackHalconRoot = @"D:\MVTec\HALCON-24.11-Progress-Steady";
        private const string NativeSubDir = @"bin\x64-win64";

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        public static void Configure()
        {
            var root = Environment.GetEnvironmentVariable("HALCONROOT");
            if (string.IsNullOrWhiteSpace(root))
            {
                root = FallbackHalconRoot;
            }

            var nativeDir = Path.Combine(root, NativeSubDir);
            if (!Directory.Exists(nativeDir))
            {
                return;
            }

            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            if (!currentPath.Split(Path.PathSeparator).Contains(nativeDir, StringComparer.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable(
                    "PATH",
                    nativeDir + Path.PathSeparator + currentPath);
            }

            // 用绝对路径显式预加载 halcon.dll，保证后续 halcondotnet 的 P/Invoke 命中 Halcon 安装目录，
            // 加载后不释放：Halcon 全程驻留进程。
            var halconDll = Path.Combine(nativeDir, "halcon.dll");
            if (File.Exists(halconDll))
            {
                LoadLibrary(halconDll);
            }
        }
    }
}
