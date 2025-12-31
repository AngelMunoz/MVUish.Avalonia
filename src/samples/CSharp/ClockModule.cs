namespace CSharp;

using System;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Data;
using System.Reactive.Linq;
using static Avalonia.Mvu.CSharp;
using NXUI.Extensions;

public static class ClockModule
{
  // Model: Just the current time
  public record Model(DateTime Time);

  // Msg: Tick event
  public abstract record Msg
  {
    public sealed record Tick(DateTime Time) : Msg;
  }

  public static (Model, Action<Action<Msg>, Action<string, Exception>>) Init() =>
      (new Model(DateTime.Now), Command.None<Msg>());

  public static (Model, Action<Action<Msg>, Action<string, Exception>>) Update(Msg msg, Model model) => msg switch
  {
    Msg.Tick t => (new Model(t.Time), Command.None<Msg>()),
    _ => (model, Command.None<Msg>())
  };

  public static Func<Action<Msg>, IDisposable> Subscribe(Model _) =>
      Subscription.Interval<Msg>(
          TimeSpan.FromSeconds(1),
          () => new Msg.Tick(DateTime.Now)
      );

  public static Control View(IObservable<Model> model, Action<Msg> _)
  {
    var timeStr = model.Select(m => m.Time.ToString("HH:mm:ss"));

    return Border()
        .CornerRadius(10)
        .Background(Brushes.Black)
        .Padding(20)
        .Child(
            TextBlock()
                .Text(timeStr)
                .FontSize(32)
                .HorizontalAlignment(HorizontalAlignment.Center)
                .Foreground(Brushes.LimeGreen)
        );
  }
}
