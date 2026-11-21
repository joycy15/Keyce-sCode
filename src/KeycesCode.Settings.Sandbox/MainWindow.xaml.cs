using System.Windows;
using System.Windows.Controls;

namespace KeycesCode.Settings.Sandbox;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        UpdateThemeTexts();
    }

    private void Activity_Checked(object sender, RoutedEventArgs e)
    {
        if (SideBarViews is null || sender is not RadioButton { Tag: string viewName }) return;

        foreach (FrameworkElement view in SideBarViews.Children)
            view.Visibility = view.Name == viewName ? Visibility.Visible : Visibility.Collapsed;
    }

    private void GitView_BranchChanged(object? sender, string branch) =>
        BranchStatus.Text = string.IsNullOrEmpty(branch) ? "" : $"⎇ {branch}";

    private async void ToggleTheme_Click(object sender, RoutedEventArgs e)
    {
        var next = App.Themes.CurrentThemeId == "dark" ? "light" : "dark";
        App.Themes.Apply(next);
        await App.Settings.SaveAsync(App.Settings.Current with { ThemeId = next });
        UpdateThemeTexts();
    }

    private void UpdateThemeTexts()
    {
        var dark = App.Themes.CurrentThemeId == "dark";
        ThemeLabel.Text = dark ? "Thème actuel : sombre" : "Thème actuel : clair";
        ThemeStatus.Text = dark ? "Sombre" : "Clair";
    }
}
