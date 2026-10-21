using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Mvu = MVUish.Avalonia;
using static MVUish.Avalonia.CSharp;
using NXUI.Extensions;
using NXUI.HotReload.Nodes;
using MvuProgram = MVUish.Avalonia.CSharp.Program;

var root = MvuProgram
    .MkProgram<Model, Msg, ElementBuilder<Border>>(Init, Update, View)
    .WithSubscription(Subscribe)
    .Run();

AppBuilder
    .Configure<Application>()
    .UsePlatformDetect()
    .UseFluentTheme()
    .StartWithClassicDesktopLifetime(
        () =>
            Window()
                .Title("MVUish.Avalonia C# Clock")
                .Width(360)
                .Height(180)
                .Content(root)
                .Mount(),
        args
    );

(Model, Action<Action<Msg>, Action<string, Exception>>) Init() =>
    (new Model(DateTime.Now), Command.None<Msg>());

(Model, Action<Action<Msg>, Action<string, Exception>>) Update(Msg msg, Model model) =>
    msg switch
    {
        Msg.Tick t => (new Model(t.Time), Command.None<Msg>()),
        _ => (model, Command.None<Msg>())
    };

CSub<Msg> Subscribe(Model _) =>
    Subscription.Interval("clock", TimeSpan.FromSeconds(1), () => new Msg.Tick(DateTime.Now));

ElementBuilder<Border> View(IObservable<Model> model, Action<Msg> _)
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

record Model(DateTime Time);

abstract record Msg
{
    public sealed record Tick(DateTime Time) : Msg;
}
