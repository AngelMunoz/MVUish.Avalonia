namespace CSharp;

using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Data;
using System.Reactive.Linq;
using static Avalonia.Mvu.CSharp;
using NXUI.Extensions;

public static class MetricsModule
{
    public record Model(int Cpu, int Ram, bool IsCritical);

    public abstract record Msg
    {
        public sealed record Generate : Msg;
        public sealed record NewMetrics(int Cpu, int Ram) : Msg;
    }

    public static (Model, Action<Action<Msg>, Action<string, Exception>>) Init() =>
        (new Model(0, 0, false), Command.OfMsg<Msg>(new Msg.Generate()));

    public static (Model, Action<Action<Msg>, Action<string, Exception>>) Update(Msg msg, Model model)
    {
        switch (msg)
        {
            case Msg.Generate:
                Func<Task<Msg>> generator = async () =>
                {
                    await Task.Delay(1000); // Simulate work
                    var rnd = new Random();
                    return new Msg.NewMetrics(rnd.Next(10, 100), rnd.Next(200, 8000));
                };
                
                return (model, Command.PerformTask(
                    (Func<object?, Task<Msg>>)(_ => generator()), 
                    null, 
                    res => res)
                );

            case Msg.NewMetrics m:
                var isCrit = m.Cpu > 80 || m.Ram > 7000;
                return (
                    new Model(m.Cpu, m.Ram, isCrit), 
                    Command.OfMsg<Msg>(new Msg.Generate())
                );

            default:
                return (model, Command.None<Msg>());
        }
    }

    public static Control View(IObservable<Model> model, Action<Msg> dispatch)
    {
        var cpuText = model.Select(m => $"CPU: {m.Cpu}%");
        var ramText = model.Select(m => $"RAM: {m.Ram} MB");
        
        var color = model
            .Select(m => m.IsCritical ? Brushes.Red : Brushes.Cyan)
            .DistinctUntilChanged();

        return Border()
            .CornerRadius(10)
            .BorderBrush(color.ToBinding(), BindingMode.OneWay)
            .BorderThickness(2)
            .Padding(15)
            .Margin(0, 10, 0, 0)
            .Child(
                StackPanel()
                    .Spacing(5)
                    .Children(
                        TextBlock().Text("System Metrics").FontWeight(FontWeight.Bold).Foreground(Brushes.White),
                        ProgressBar().Minimum(0).Maximum(100).Value(model.Select(m => (double)m.Cpu).ToBinding(), BindingMode.OneWay).Height(10),
                        TextBlock().Text(cpuText.ToBinding(), BindingMode.OneWay).FontSize(12),
                        TextBlock().Text(ramText.ToBinding(), BindingMode.OneWay).FontSize(12)
                    )
            );
    }
}