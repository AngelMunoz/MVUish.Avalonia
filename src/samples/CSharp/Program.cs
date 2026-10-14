using CSharp;


Window View() =>
    Window()
        .Title("NXUI + Avalonia.MVU")
        .Width(300)
        .Height(300)
        .Content(MainView.Build())
        .Mount();


AppBuilder
  .Configure<Application>()
  .UsePlatformDetect()
  .UseFluentTheme(ThemeVariant.Dark)
  .WithApplicationName("NXUI + Avalonia.MVU")
  .StartWithClassicDesktopLifetime(View, args);
