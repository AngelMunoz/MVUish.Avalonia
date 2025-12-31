namespace CSharp;

using static Avalonia.Mvu.CSharp;

public static class MainView
{
  private sealed record State(int Count = 0, string Name = "", bool NameFound = false);

  private abstract record Message
  {
    public sealed record Increment : Message;

    public sealed record Decrement : Message;

    public sealed record Reset : Message;

    public sealed record NameFound(bool Found) : Message;

    public sealed record SetName(string Name) : Message;
  }

  // Helper method for complex update logic
  private static (State, Action<Action<Message>>) UpdateSetname(State state, string name)
  {
    static bool wasNameFound(string maybeName) => maybeName.Equals("peter", StringComparison.InvariantCultureIgnoreCase);

    static Message onSuccess(bool nameFound) => new Message.NameFound(nameFound);

    // Use the C# friendly Command helper
    var cmd = Command.Perform(wasNameFound, name, onSuccess);

    return (state with { Name = name }, cmd);
  }

  public static Control Build(Window window)
  {
    // View function is now separate and passed to the Program
    Control View(IObservable<State> state, Action<Message> dispatch)
    {
      var counterText = state
        .Select(s => s.Count)
        .DistinctUntilChanged()
        .Select(count => $"You clicked {count} times");

      var nameFound = state
        .Select(s => s.NameFound)
        .DistinctUntilChanged();

      var actionPerformed = state
        .Select(s => s.Name)
        .DistinctUntilChanged()
        .Select(name => $"Action Performed: {name}");

      void IncrementOnClick(Button sender, IObservable<RoutedEventArgs> e) =>
        e.Subscribe(_ => dispatch(new Message.Increment()));

      void CheckText(TextBox sender, IObservable<string> e) =>
        e.Throttle(TimeSpan.FromMilliseconds(250))
         .Subscribe(name => dispatch(new Message.SetName(name)));

      return StackPanel()
        .Children(
          Button().Content("Click me!").OnClick(IncrementOnClick),
          TextBox().Text(window.BindTitle()),
          TextBlock().Text(counterText, BindingMode.OneWay),
          TextBlock().Text(actionPerformed, BindingMode.OneWay),
          StackPanel()
            .Spacing(4.0)
            .Children(
              Label().Content("Find the name!"),
              TextBox().Watermark("Starts with P ends with R").OnText(CheckText)
            ),
          CheckBox()
            .IsChecked(nameFound.ToBinding(), BindingMode.OneWay)
            .IsEnabled(false)
            .Content("Name Found"),
          StackPanel()
            .Margin(new Thickness(10))
            .Children(
              Label().Content("Elmish Counter"),
              CounterModule.Build()
            )
        );
    }

    return Program.MkProgram<State, Message, Control>(
        init: () => (new State(), Command.None<Message>()),
        update: (message, state) => message switch
        {
          Message.Increment => (state with { Count = state.Count + 1 }, Command.OfMsg<Message>(new Message.SetName("Increment"))),
          Message.Decrement => (state with { Count = state.Count - 1 }, Command.OfMsg<Message>(new Message.SetName("Decrement"))),
          Message.Reset => (state with { Count = 0 }, Command.OfMsg<Message>(new Message.SetName("Reset"))),
          Message.NameFound(var found) => (state with { NameFound = found }, Command.None<Message>()),
          Message.SetName({ } name) => UpdateSetname(state, name),
          _ => (state, Command.None<Message>())
        },
        view: View
    )
    .WithConsoleTrace()
    .Run();
  }
}
