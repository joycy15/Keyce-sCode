using System.Windows;

namespace KeycesCode.Settings.Sandbox;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        UpdateLabel();
    }

    private async void ToggleButton_Click(object sender, RoutedEventArgs e)
    {
        var next = App.Themes.CurrentThemeId == "dark" ? "light" : "dark";

        App.Themes.Apply(next);
        await App.Settings.SaveAsync(App.Settings.Current with { ThemeId = next });

        UpdateLabel();
    }

    private void UpdateLabel() =>
        ThemeLabel.Text = $"Thème actuel : {App.Themes.CurrentThemeId}";
}