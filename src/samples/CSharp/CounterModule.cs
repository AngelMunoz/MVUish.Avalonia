namespace CSharp;

using static Avalonia.Mvu.CSharp;

/// <summary>
/// This static class contains an Elmish View that matches the MVU paradigm.
/// </summary>
public static class CounterModule
{
  /// <summary>
  /// The M in MVU this represents the model or also called state
  /// that contains the data of the current view and it is used to populate it.
  /// </summary>
  private sealed record Model(int Count = 0, string Label = "Type Something :)");

  /// <summary>
  /// As there are no Discriminated Unions in C# we can make a record that contains
  /// our "cases" for the messages that we want to send to the update function.
  /// </summary>
  private abstract record Message
  {
    public sealed record Increment : Message;
    public sealed record Decrement : Message;
    public sealed record SetText(string Text) : Message;
  }

  /// <summary>
  /// This build method prepares our MVU based control.
  /// </summary>
  /// <returns>Our Built UI based on the current state of our MVU</returns>
  public static Control Build()
  {
      return Program.MkProgram<Model, Message, Control>(
          init: () => (new Model(10), Command.None<Message>()),
          update: (message, model) => message switch
          {
              Message.Increment => (model with { Count = model.Count + 1 }, Command.None<Message>()),
              Message.Decrement => (model with { Count = model.Count - 1 }, Command.None<Message>()),
              Message.SetText(var text) => (model with { Label = text }, Command.None<Message>()),
              _ => (model, Command.None<Message>())
          },
          view: (modelObs, dispatch) =>
          {
              var countText = modelObs
                .Select(m => m.Count)
                .DistinctUntilChanged()
                .Select(c => $"You clicked {c} times");

              var labelText = modelObs
                .Select(m => m.Label)
                .DistinctUntilChanged();

              var dynamicText = modelObs
                .Select(m => m.Count == 20 ? "You have reached 20!" : "Not reached 20 yet!")
                .DistinctUntilChanged();

              return StackPanel()
                  .Spacing(4.0)
                  .Children(
                    Button()
                      .Content("Increment").OnClick((_, o) =>
                        o.Subscribe(_ => dispatch(new Message.Increment()))),

                    TextBlock().Text(countText, BindingMode.OneWay),

                    StackPanel()
                      .Spacing(4.0)
                      .Children(
                        Label().Content(labelText.ToBinding(), BindingMode.OneWay),

                        // State preserved!
                        TextBox()
                          .Text(labelText.ToBinding(), BindingMode.OneWay)
                          .OnText((_, o) =>
                            {
                              o.Subscribe(text => dispatch(new Message.SetText(text)));
                            })
                        ),

                    TextBlock().Text(dynamicText, BindingMode.OneWay),

                    Button()
                      .Content("Decrement").OnClick((_, o) =>
                        o.Subscribe(_ => dispatch(new Message.Decrement())))
                  );
          }
      )
      .Run();
  }
}
