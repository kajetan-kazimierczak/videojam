using System.Reflection;
using System.Windows;
using Microsoft.Extensions.Logging;

namespace VideoJam;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application {
	/// <inheritdoc />
	protected override void OnStartup(StartupEventArgs e) {
		base.OnStartup(e);

		string? exePath    = Environment.ProcessPath;
		Version? version   = Assembly.GetEntryAssembly()?.GetName().Version;
		DateTime exeStamp  = exePath is not null ? new FileInfo(exePath).LastWriteTime : DateTime.MinValue;

		using ILoggerFactory loggerFactory = LoggerFactory.Create(b =>
			b.AddConsole().SetMinimumLevel(LogLevel.Information));

		loggerFactory
			.CreateLogger<App>()
			.LogInformation(
				"VideoJam {Version} — built {Timestamp:yyyy-MM-dd HH:mm:ss}",
				version?.ToString(3) ?? "unknown",
				exeStamp);
	}
}
