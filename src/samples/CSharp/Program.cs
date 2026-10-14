using CSharp;


Window View() =>
    Window()
        .Title("NXUI + MVUish.Avalonia")
        .Width(420)
        .Height(840)
        .Content(MainView.Build())
        .Mount();


AppBuilder
  .Configure<Application>()
  .UsePlatformDetect()
  .UseFluentTheme(ThemeVariant.Dark)
  .WithApplicationName("NXUI + MVUish.Avalonia")
  .StartWithClassicDesktopLifetime(View, args);
