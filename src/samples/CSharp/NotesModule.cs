namespace CSharp;

using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Data;
using System.Reactive.Linq;
using static Avalonia.Mvu.CSharp;
using NXUI.Extensions;
using NotesBinding = Avalonia.Mvu.Collections.CollectionExtensions;

public static class NotesModule
{
  public record Note(Guid Id, string Text)
  {
    public override string ToString() => Text;
  }

  public record Model(IReadOnlyList<Note> Notes);

  public abstract record Msg
  {
    public sealed record Add(string Text) : Msg;
    public sealed record Remove(Guid Id) : Msg;
    public sealed record Reverse : Msg;
  }

  public static (Model, Action<Action<Msg>, Action<string, Exception>>) Init()
  {
    Note Seed(string text) => new(Guid.NewGuid(), text);

    return (
        new Model([Seed("First"), Seed("Second"), Seed("Third")]),
        Command.None<Msg>()
    );
  }

  public static (Model, Action<Action<Msg>, Action<string, Exception>>) Update(Msg msg, Model model)
  {
    switch (msg)
    {
      case Msg.Add a when !string.IsNullOrWhiteSpace(a.Text):
        var note = new Note(Guid.NewGuid(), a.Text.Trim());
        return (new Model([note, .. model.Notes]), Command.None<Msg>());

      case Msg.Remove r:
        var kept = model.Notes.Where(n => n.Id != r.Id).ToArray();
        return (new Model(kept), Command.None<Msg>());

      case Msg.Reverse:
        return (new Model(model.Notes.Reverse().ToArray()), Command.None<Msg>());

      default:
        return (model, Command.None<Msg>());
    }
  }

  public static ElementBuilder<Border> View(IObservable<Model> model, Action<Msg> dispatch)
  {
    var notes = model.Select(m => m.Notes);
    var count = model.Select(m => $"{m.Notes.Count} notes");

    // Each row owns state the model never sees (the checkbox and the text
    // box). The keyed binding matches rows by Id and reuses containers, so
    // that state travels with its note through adds, removes, and reorders.
    var rowTemplate = new FuncDataTemplate<Note>(
        (note, _) =>
            StackPanel()
                .Orientation(Orientation.Horizontal)
                .Spacing(8)
                .Children(
                    CheckBox(),
                    TextBlock()
                        .VerticalAlignment(VerticalAlignment.Center)
                        .Text(new Binding("Text")),
                    TextBox().Width(140).PlaceholderText("row state lives here"),
                    Button()
                        .Content("x")
                        .OnClickHandler((_, _) => dispatch(new Msg.Remove(note.Id)))
                )
                .Mount(),
        supportsRecycling: false
    );

    var list = ListBox()
        .ItemTemplate(rowTemplate)
        .MinHeight(160)
        .MaxHeight(200)
        // NXUI defines its own BindItemsSource extension for the raw
        // ItemsSource property, so call the adapter by its type name.
        // WithAction runs once the builder materializes the control.
        .WithAction(lb => NotesBinding.BindItemsSource(lb, notes, n => n.Id));

    var draft = "";

    var root = Border()
        .CornerRadius(10)
        .BorderThickness(2)
        .BorderBrush(Brushes.Teal)
        .Padding(15)
        .Margin(0, 10, 0, 0)
        .Child(
            StackPanel()
                .Spacing(8)
                .Children(
                    TextBlock()
                        .Text("Notes")
                        .FontWeight(FontWeight.Bold)
                        .Foreground(Brushes.White),
                    TextBlock().Text(count).FontSize(12),
                    StackPanel()
                        .Orientation(Orientation.Horizontal)
                        .Spacing(5)
                        .Children(
                            TextBox(out var input)
                                .Width(160)
                                .PlaceholderText("new note text"),
                            Button()
                                .Content("Add")
                                .OnClickHandler((_, _) => dispatch(new Msg.Add(draft))),
                            Button()
                                .Content("Reverse")
                                .OnClickHandler((_, _) => dispatch(new Msg.Reverse()))
                        ),
                    list
                )
        );

    input.ObserveText().Subscribe(text => draft = text ?? string.Empty);

    return root;
  }
}
