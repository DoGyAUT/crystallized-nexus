using Avalonia;

namespace CNLauncher;

static class Program
{
	// Avalonia needs the entry point to stay free of any code that touches the toolkit
	// before AppMain runs, so the builder lives in its own method.
	[STAThread]
	public static int Main(string[] args)
		=> BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

	public static AppBuilder BuildAvaloniaApp()
		=> AppBuilder.Configure<App>()
			.UsePlatformDetect()
			.WithInterFont()
			.LogToTrace();
}
