using System;
using System.Windows.Forms;

namespace DepthFusionApp
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 必须在第一次调用任何 Halcon 算子之前，把 halcon.dll 所在目录加入本机 DLL 搜索路径并预加载
            HalconRuntime.Configure();

            Application.Run(new MainForm());
        }
    }
}
