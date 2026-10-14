using CSharp;


Window View() =>
    Window()
        .Title("NXUI + Avalonia.MVU")
        .Width(420)
        .Height(840)
        .Content(MainView.Build())
        .Mount();


AppBuilder
  .Configure<Application>()
  .UsePlatformDetect()
  .UseFluentTheme(ThemeVariant.Dark)
  .WithApplicationName("NXUI + Avalonia.MVU")
  .StartWithClassicDesktopLifetime(View, args);
