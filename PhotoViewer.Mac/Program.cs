using PhotoViewer.Mac.Native;

namespace PhotoViewer.Mac;

sealed class Program
{
    /// <summary>原生 AppKit 入口；历史启动参数（如 <c>--native</c>）由原生路径统一忽略。</summary>
    [STAThread]
    public static void Main(string[] args) => MacNativeApplication.Run(args);
}
