using CSharp;


Window View()
{
  Window(out var window)
    .Title("NXUI + Avalonia.MVU")
    .Width(300)
    .Height(300)
    .Content(MainView.Build(window));
  return window;
}


AppBuilder
  .Configure<Application>()
  .UsePlatformDetect()
  .UseFluentTheme(ThemeVariant.Dark)
  .WithApplicationName("NXUI + Avalonia.MVU")
  .StartWithClassicDesktopLifetime(View, args);
