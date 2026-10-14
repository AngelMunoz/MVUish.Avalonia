using System;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using static MVUish.Avalonia.CSharp;
using NXUI.Extensions;
using NXUI.HotReload.Nodes;
using MvuProgram = MVUish.Avalonia.CSharp.Program;

internal static class Program
{
  private record Model(DateTime Time);

  private abstract record Msg
  {
    public sealed record Tick(DateTime Time) : Msg;
  }

  private static (Model, Action<Action<Msg>, Action<string, Exception>>) Init() =>
      (new Model(DateTime.Now), Command.None<Msg>());

  private static (Model, Action<Action<Msg>, Action<string, Exception>>) Update(
      Msg msg,
      Model model
  ) =>
      msg switch
      {
          Msg.Tick t => (new Model(t.Time), Command.None<Msg>()),
          _ => (model, Command.None<Msg>())
      };

  private static Func<Action<Msg>, IDisposable> Subscribe(Model _) =>
      Subscription.Interval(TimeSpan.FromSeconds(1), () => new Msg.Tick(DateTime.Now));

  private static ElementBuilder<Border> View(IObservable<Model> model, Action<Msg> _)
  {
    var time = model.Select(m => m.Time.ToString("HH:mm:ss"));

    return Border()
        .CornerRadius(10)
        .Background(Brushes.Black)
        .Padding(20)
        .Child(
            TextBlock()
                .Text(time)
                .FontSize(32)
                .HorizontalAlignment(HorizontalAlignment.Center)
                .Foreground(Brushes.LimeGreen)
        );
  }

  private static void Main(string[] args)
  {
    var root = MvuProgram
        .MkProgram<Model, Msg, ElementBuilder<Border>>(Init, Update, View)
        .WithSubscription(Subscribe)
        .Run();

    Window MainWindow() =>
        Window()
            .Title("MVUish.Avalonia C# Clock")
            .Width(360)
            .Height(180)
            .Content(root)
            .Mount();

    AppBuilder
        .Configure<Application>()
        .UsePlatformDetect()
        .UseFluentTheme()
        .StartWithClassicDesktopLifetime(MainWindow, args);
  }
}
