using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Starshot.Features.Screenshot;

namespace Starshot;

public static partial class AppConfig
{
    private static IServiceProvider _serviceProvider;

    /// <summary>
    /// 本次启动检测到的版本迁移来源版本（不落盘，仅供匿名统计读取一次）。
    /// </summary>
    private static string? _pendingVersionFrom;

    /// <summary>
    /// 取走版本迁移的来源版本（取后清空）。无迁移时返回 null。
    /// </summary>
    internal static string? TakePendingVersionFrom()
    {
        string? from = _pendingVersionFrom;
        _pendingVersionFrom = null;
        return from;
    }

    private static void BuildServiceProvider()
    {
        if (_serviceProvider == null)
        {
            var minLevel = AppConfig.LogLevel switch
            {
                1 => Serilog.Events.LogEventLevel.Error,
                2 => Serilog.Events.LogEventLevel.Warning,
                4 => Serilog.Events.LogEventLevel.Debug,
                _ => Serilog.Events.LogEventLevel.Information,
            };
            var cfg = new LoggerConfiguration().Enrich.FromLogContext().MinimumLevel.Is(minLevel);
            if (AppConfig.LogLevel != 0)
            {
                cfg.WriteTo.File(
                    path: LogFile,
                    shared: true,
                    outputTemplate: $$"""[{Timestamp:HH:mm:ss.fff}] [{Level:u4}] [{{Environment.ProcessId}}] {SourceContext}{NewLine}{Message}{NewLine}{Exception}{NewLine}"""
                );
            }
            Log.Logger = cfg.CreateLogger();
            // 版本行与关于页标签同款：Debug 只显示 Debug；Release 带版本 + 安装版 Installer 后缀
#if DEBUG
            string versionTag = "Debug";
            bool trackVersionChange = false;
#else
            string versionTag = $"Release {AppVersion}{(Installer ? " Installer" : "")}";
            // 本地构建不参与版本迁移判定，避免把 0.0.0-local 记成一次更新
            bool trackVersionChange = !AppVersion.EndsWith(
                "-local",
                StringComparison.OrdinalIgnoreCase
            );
#endif
            // 版本迁移：更新成功必然伴随进程重启，启动时对比上次运行版本即可。
            // 行插在 Welcome 与 Runtime 之间，形如 "Version changed: 2.5.3 Installer -> 2.5.4 Installer"
            string versionChangedLine = "";
            string? previousVersion = null;
            if (trackVersionChange)
            {
                string? lastVersion = LastAppVersion;
                LastAppVersion = AppVersion;
                if (
                    !string.IsNullOrEmpty(lastVersion)
                    && !string.Equals(lastVersion, AppVersion, StringComparison.OrdinalIgnoreCase)
                )
                {
                    previousVersion = lastVersion;
                    string suffix = Installer ? " Installer" : "";
                    versionChangedLine =
                        $"\r\nVersion changed: {lastVersion}{suffix} -> {AppVersion}{suffix}";
                }
            }
            Log.Information(
                $"Welcome to Starshot {versionTag}{versionChangedLine}\r\nRuntime: {Environment.Version}\r\nCommand Line: {Environment.CommandLine}"
            );
            // 同一条版本迁移留给心跳上报（进程内字段，不落盘）。
            // 不能在这里直接调 TelemetryService：它的静态初始化会回调 GetLogger → BuildServiceProvider，
            // 而此时 _serviceProvider 尚未赋值，会重入本方法。
            _pendingVersionFrom = previousVersion;

            var sc = new ServiceCollection();
            sc.AddMemoryCache();
            sc.AddLogging(c => c.AddSerilog(Log.Logger));

            sc.AddSingleton<ScreenCaptureService>();
            sc.AddSingleton<Starshot.Features.Recording.VideoRecordService>();

            _serviceProvider = sc.BuildServiceProvider();
        }
    }

    public static T GetService<T>()
    {
        BuildServiceProvider();
        return _serviceProvider.GetService<T>()!;
    }

    public static ILogger<T> GetLogger<T>()
    {
        BuildServiceProvider();
        return _serviceProvider.GetService<ILogger<T>>()!;
    }

    /// <summary>
    /// 非泛型取日志（静态类不能当泛型参数时用）。同时确保日志系统已初始化。
    /// </summary>
    public static Microsoft.Extensions.Logging.ILogger GetLogger(string categoryName)
    {
        BuildServiceProvider();
        return _serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(categoryName);
    }
}
