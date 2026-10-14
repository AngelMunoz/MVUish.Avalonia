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
using NXUI.HotReload.Nodes;

public static class MainView
{
  // PARENT MODEL
  public record Model(
      ClockModule.Model Clock,
      MetricsModule.Model Metrics,
      NotesModule.Model Notes,
      string Title
  );

  // PARENT MSG
  public abstract record Msg
  {
    public sealed record Clock(ClockModule.Msg M) : Msg;
    public sealed record Metrics(MetricsModule.Msg M) : Msg;
    public sealed record Notes(NotesModule.Msg M) : Msg;
    public sealed record Rename(string NewTitle) : Msg;
  }

  public static ElementBuilder<DockPanel> Build()
  {
    // INIT
    (Model, Action<Action<Msg>, Action<string, Exception>>) Init()
    {
      var (cModel, cCmd) = ClockModule.Init();
      var (mModel, mCmd) = MetricsModule.Init();
      var (nModel, nCmd) = NotesModule.Init();

      var model = new Model(cModel, mModel, nModel, "C# MVU Dashboard");

      var batch = Command.Batch([
                Command.Map<ClockModule.Msg, Msg>(m => new Msg.Clock(m), cCmd),
                Command.Map<MetricsModule.Msg, Msg>(m => new Msg.Metrics(m), mCmd),
                Command.Map<NotesModule.Msg, Msg>(m => new Msg.Notes(m), nCmd)
            ]);

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
              Command.Map<MetricsModule.Msg, Msg>(x => new Msg.Metrics(x), mCmd)
          );

        case Msg.Notes n:
          var (nModel, nCmd) = NotesModule.Update(n.M, model.Notes);
          return (
              model with { Notes = nModel },
              Command.Map<NotesModule.Msg, Msg>(x => new Msg.Notes(x), nCmd)
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
      return Subscription.Batch([
          Subscription.Map<ClockModule.Msg, Msg>(
                    m => new Msg.Clock(m),
                    ClockModule.Subscribe(model.Clock)
                )
      ]);
    }

    // VIEW
    ElementBuilder<DockPanel> View(IObservable<Model> model, Action<Msg> dispatch)
    {
      // Lens
      var clockObs = model.Select(m => m.Clock);
      var metricsObs = model.Select(m => m.Metrics);
      var notesObs = model.Select(m => m.Notes);

      // Dispatchers
      void dispatchClock(ClockModule.Msg m) => dispatch(new Msg.Clock(m));
      void dispatchMetrics(MetricsModule.Msg m) => dispatch(new Msg.Metrics(m));
      void dispatchNotes(NotesModule.Msg m) => dispatch(new Msg.Notes(m));

      var root = DockPanel()
          .LastChildFill(true)
          .Children(
              TextBlock()
                  .Dock(Dock.Top)
                  .Text(model.Select(m => m.Title))
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

                      // Child 3
                      NotesModule.View(notesObs, dispatchNotes),

                      // Parent Control
                      StackPanel()
                          .Spacing(5)
                          .Children(
                              Label().Content("Dashboard Settings").Foreground(Brushes.LightGray),
                              TextBox(out var titleBox)
                                  .Text(model.Select(m => m.Title))
                          )
                  )
          );

      titleBox.ObserveText().Subscribe(t =>
          dispatch(new Msg.Rename(t ?? string.Empty))
      );

      return root;
    }

    return Program.MkProgram(
        init: Init,
        update: Update,
        view: View
    )
    .WithSubscription(Subscribe)
    .WithErrorHandler((msg, ex) =>
    {
      Debug.WriteLine($"[GLOBAL ERROR] {msg}: {ex.Message}");
      Console.Error.WriteLine($"[GLOBAL ERROR] {msg}: {ex}");
    })
    .Run();
  }
}
