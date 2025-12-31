namespace CSharp;

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Data;
using System.Reactive.Linq;
using static Avalonia.Mvu.CSharp;
using NXUI.Extensions;

public static class MainView
{
    // PARENT MODEL
    public record Model(
        ClockModule.Model Clock, 
        MetricsModule.Model Metrics, 
        string Title
    );

    // PARENT MSG
    public abstract record Msg
    {
        public sealed record Clock(ClockModule.Msg M) : Msg;
        public sealed record Metrics(MetricsModule.Msg M) : Msg;
        public sealed record Rename(string NewTitle) : Msg;
    }

    public static Control Build(Window window)
    {
        // INIT
        (Model, Action<Action<Msg>, Action<string, Exception>>) Init()
        {
            var (cModel, cCmd) = ClockModule.Init();
            var (mModel, mCmd) = MetricsModule.Init();

            var model = new Model(cModel, mModel, "C# MVU Dashboard");

            var batch = Command.Batch(new[] {
                Command.Map<ClockModule.Msg, Msg>(m => new Msg.Clock(m), cCmd),
                Command.Map<MetricsModule.Msg, Msg>(m => new Msg.Metrics(m), mCmd)
            });

            return (model, batch);
        }

        // UPDATE
        (Model, Action<Action<Msg>, Action<string, Exception>>) Update(Msg msg, Model model)
        {
            switch (msg)
            {
                case Msg.Clock c:
                    var (cModel, cCmd) = ClockModule.Update(c.M, model.Clock);
                    return (
                        model with { Clock = cModel }, 
                        Command.Map<ClockModule.Msg, Msg>(m => new Msg.Clock(m), cCmd)
                    );

                case Msg.Metrics m:
                    var (mModel, mCmd) = MetricsModule.Update(m.M, model.Metrics);
                    return (
                        model with { Metrics = mModel },
                        Command.Map<MetricsModule.Msg, Msg>(m => new Msg.Metrics(m), mCmd)
                    );

                case Msg.Rename r:
                    return (model with { Title = r.NewTitle }, Command.None<Msg>());

                default:
                    return (model, Command.None<Msg>());
            }
        }

        // SUBSCRIBE
        Func<Action<Msg>, IDisposable> Subscribe(Model model)
        {
            return Subscription.Batch(new[] {
                Subscription.Map<ClockModule.Msg, Msg>(
                    m => new Msg.Clock(m), 
                    ClockModule.Subscribe(model.Clock)
                )
            });
        }

        // VIEW
        Control View(IObservable<Model> model, Action<Msg> dispatch)
        {
            // Lens
            var clockObs = model.Select(m => m.Clock);
            var metricsObs = model.Select(m => m.Metrics);

            // Dispatchers
            Action<ClockModule.Msg> dispatchClock = m => dispatch(new Msg.Clock(m));
            Action<MetricsModule.Msg> dispatchMetrics = m => dispatch(new Msg.Metrics(m));

            return DockPanel()
                .LastChildFill(true)
                .Children(
                    TextBlock()
                        .Dock(Dock.Top)
                        .Text(model.Select(m => m.Title).ToBinding(), BindingMode.OneWay)
                        .FontSize(24)
                        .HorizontalAlignment(HorizontalAlignment.Center)
                        .Margin(10)
                        .Foreground(Brushes.White),

                    StackPanel()
                        .Margin(20)
                        .Spacing(20)
                        .Children(
                            // Child 1
                            ClockModule.View(clockObs, dispatchClock),
                            
                            // Child 2
                            MetricsModule.View(metricsObs, dispatchMetrics),

                            // Parent Control
                            StackPanel()
                                .Spacing(5)
                                .Children(
                                    Label().Content("Dashboard Settings").Foreground(Brushes.LightGray),
                                    TextBox()
                                        .Text(model.Select(m => m.Title).ToBinding(), BindingMode.OneWay)
                                        .OnText((_, obs) => 
                                            obs.Subscribe(t => dispatch(new Msg.Rename(t)))
                                        )
                                )
                        )
                );
        }

        return Program.MkProgram<Model, Msg, Control>(
            init: Init,
            update: Update,
            view: View
        )
        .WithSubscription(Subscribe)
        .WithErrorHandler((msg, ex) => {
            System.Diagnostics.Debug.WriteLine($"[GLOBAL ERROR] {msg}: {ex.Message}");
            Console.Error.WriteLine($"[GLOBAL ERROR] {msg}: {ex}");
        })
        .Run();
    }
}