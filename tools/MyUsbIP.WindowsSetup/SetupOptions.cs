internal sealed record SetupOptions(string Mode, string? PackagePath, bool Quiet, bool VerifyOnly, int TimeoutSeconds)
{
    public static SetupOptions Parse(string[] args)
    {
        string mode = "auto";
        string? package = null;
        bool quiet = false, verify = false;
        int timeout = 300;
        for (var i = 0; i < args.Length; i++)
        {
            string Value() => ++i < args.Length ? args[i] : throw new ArgumentException($"缺少参数值: {args[i - 1]}");
            switch (args[i].ToLowerInvariant())
            {
                case "--mode": mode = Value().ToLowerInvariant(); break;
                case "--package": package = Path.GetFullPath(Value()); break;
                case "--quiet": quiet = true; break;
                case "--verify-only": verify = true; break;
                case "--timeout-seconds":
                    if (!int.TryParse(Value(), out timeout) || timeout is < 1 or > 3600)
                        throw new ArgumentException("下载超时必须是 1～3600 秒。");
                    break;
                default: throw new ArgumentException($"未知参数: {args[i]}");
            }
        }
        if (mode is not ("auto" or "online" or "offline"))
            throw new ArgumentException("--mode 必须为 auto、online 或 offline。");
        if (package is not null && mode == "online")
            throw new ArgumentException("--package 不能与 --mode online 一起使用。");
        return new(mode, package, quiet, verify, timeout);
    }

    public static void PrintHelp() => Console.WriteLine("""
        MyUsbIP NativeAOT Windows Setup
          --mode auto|online|offline   默认 auto，优先本地/内置安装包
          --package <ZIP>              指定离线包，缺失或损坏时直接失败
          --quiet                      无按键等待，适合自动部署
          --verify-only                验证并解包，不修改驱动、程序或系统配置
          --timeout-seconds <1..3600>   在线下载总超时，默认 300 秒
          --help                       显示帮助
        在线 EXE 与离线 ZIP 放在同一目录时，auto 模式自动离线安装。
        离线 EXE 内置全部安装文件，--mode offline 保证不发起网络请求。
        """);
}
