using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Aksara.Desktop.Views;

public enum LocalDraftChoice
{
    KeepEditing,
    SaveDraft,
    Discard
}

public sealed class LocalDraftPromptWindow : Window
{
    private LocalDraftChoice _choice = LocalDraftChoice.KeepEditing;

    private LocalDraftPromptWindow(string formName)
    {
        Title = "AKSARA - Data Belum Disimpan";
        Width = 510;
        Height = 230;
        MinWidth = 510;
        MinHeight = 230;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White;
        Content = BuildContent(formName);
    }

    public static LocalDraftChoice Show(Window? owner, string formName)
    {
        var dialog = new LocalDraftPromptWindow(formName);
        if (owner is { IsVisible: true }) dialog.Owner = owner;
        dialog.ShowDialog();
        return dialog._choice;
    }

    private UIElement BuildContent(string formName)
    {
        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(new TextBlock
        {
            Text = "Data belum disimpan",
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Foreground = Brush("#74101C")
        });

        var message = new TextBlock
        {
            Text = $"Isian {formName} belum disimpan. Simpan sebagai draft lokal agar dapat dilanjutkan saat form dibuka kembali, atau buang seluruh isian.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Margin = new Thickness(0, 16, 0, 18),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(message, 1);
        root.Children.Add(message);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(ActionButton("Simpan Draft", 112, () => Complete(LocalDraftChoice.SaveDraft)));
        actions.Children.Add(ActionButton("Buang Isian", 108, () => Complete(LocalDraftChoice.Discard)));
        actions.Children.Add(ActionButton("Kembali", 92, () => Complete(LocalDraftChoice.KeepEditing)));
        Grid.SetRow(actions, 2);
        root.Children.Add(actions);
        return root;
    }

    private Button ActionButton(string text, double width, Action action)
    {
        var button = new Button
        {
            Content = text,
            Width = width,
            Height = 32,
            Margin = new Thickness(8, 0, 0, 0)
        };
        button.Click += (_, _) => action();
        return button;
    }

    private void Complete(LocalDraftChoice choice)
    {
        _choice = choice;
        DialogResult = true;
    }

    private static SolidColorBrush Brush(string value) => new((Color)ColorConverter.ConvertFromString(value));
}
