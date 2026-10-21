namespace CSharp;

using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Data;
using System.Reactive.Linq;
using Mvu = MVUish.Avalonia;
using static MVUish.Avalonia.CSharp;
using NXUI.Extensions;
using NotesBinding = MVUish.Avalonia.Collections.CollectionExtensions;

public static class NotesModule
{
  public record Note(Guid Id, string Text, bool Checked, string RowText)
  {
    public override string ToString() => Text;
  }

  public record Model(IReadOnlyList<Note> Notes);

  public abstract record Msg
  {
    public sealed record Add(string Text) : Msg;
    public sealed record Remove(Guid Id) : Msg;
    public sealed record Reverse : Msg;
    public sealed record SetChecked(Guid Id, bool Checked) : Msg;
    public sealed record SetRowText(Guid Id, string Text) : Msg;
  }

  public static (Model, Action<Action<Msg>, Action<string, Exception>>) Init()
  {
    Note Seed(string text) => new(Guid.NewGuid(), text, false, "");

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
        var note = new Note(Guid.NewGuid(), a.Text.Trim(), false, "");
        return (new Model([note, .. model.Notes]), Command.None<Msg>());

      case Msg.Remove r:
        var kept = model.Notes.Where(n => n.Id != r.Id).ToArray();
        return (new Model(kept), Command.None<Msg>());

      case Msg.Reverse:
        return (new Model(model.Notes.Reverse().ToArray()), Command.None<Msg>());

      case Msg.SetChecked c:
        var checkedNotes = model.Notes
            .Select(n => n.Id == c.Id ? n with { Checked = c.Checked } : n)
            .ToArray();
        return (new Model(checkedNotes), Command.None<Msg>());

      case Msg.SetRowText t:
        var textNotes = model.Notes
            .Select(n => n.Id == t.Id ? n with { RowText = t.Text } : n)
            .ToArray();
        return (new Model(textNotes), Command.None<Msg>());

      default:
        return (model, Command.None<Msg>());
    }
  }

  public static ElementBuilder<Border> View(IObservable<Model> model, Action<Msg> dispatch)
  {
    var notes = model.Select(m => m.Notes);
    var count = model.Select(m => $"{m.Notes.Count} notes");

    // Rows are recyclable: the ListBox virtualizes, so Avalonia reuses a
    // row's control tree for a different note while the list scrolls. The
    // row keeps no state of its own; every control rehydrates from the note
    // in its DataContext. Reflection bindings would do the same, but they
    // need reflection, so this code-built view syncs explicitly and stays
    // trimming/AOT friendly.
    var rowTemplate = new FuncDataTemplate<Note>(
        (note, _) =>
        {
            var checkBox = new CheckBox { IsChecked = note.Checked };
            checkBox.IsCheckedChanged += (_, _) =>
            {
                if (checkBox.DataContext is Note current && (checkBox.IsChecked ?? false) != current.Checked)
                    dispatch(new Msg.SetChecked(current.Id, checkBox.IsChecked ?? false));
            };

            var textBlock = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Text = note.Text
            };

            var textBox = new TextBox
            {
                Width = 140,
                PlaceholderText = "row state lives here",
                Text = note.RowText
            };

            // Commit on focus loss. Dispatching per keystroke races the async
            // model update: the row is refreshed with the previous model value
            // and the new character is overwritten.
            textBox.LostFocus += (_, _) =>
            {
                if (textBox.DataContext is Note current && textBox.Text != current.RowText)
                    dispatch(new Msg.SetRowText(current.Id, textBox.Text));
            };

            var remove = new Button { Content = "x" };
            remove.Click += (_, _) =>
            {
                if (remove.DataContext is Note note)
                    dispatch(new Msg.Remove(note.Id));
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(checkBox);
            row.Children.Add(textBlock);
            row.Children.Add(textBox);
            row.Children.Add(remove);

            // Rehydrate the row when the container is recycled for another
            // note. A new note instance for the same id (an update, e.g. a
            // checkbox toggle) keeps the text box untouched, so text being
            // typed is not overwritten by the committed model value.
            var syncedId = note.Id;
            row.DataContextChanged += (_, _) =>
            {
                if (row.DataContext is Note current)
                {
                    checkBox.IsChecked = current.Checked;
                    textBlock.Text = current.Text;

                    if (current.Id != syncedId)
                    {
                        textBox.Text = current.RowText;
                        syncedId = current.Id;
                    }
                }
            };

            return row;
        },
        supportsRecycling: true
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
