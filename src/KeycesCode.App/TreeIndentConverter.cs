using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace KeycesCode.App;

// Explorateur : décalage à gauche d'un élément selon sa profondeur dans l'arbre.
// La surbrillance (survol, sélection) prend ainsi toute la largeur de la ligne, comme dans VS Code.
public sealed class TreeIndentConverter : IValueConverter
{
    public double Indent { get; set; } = 10;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not TreeViewItem item) return new Thickness(0);

        int depth = 0;
        ItemsControl? parent = ItemsControl.ItemsControlFromItemContainer(item);
        while (parent is TreeViewItem parentItem)
        {
            depth++;
            parent = ItemsControl.ItemsControlFromItemContainer(parentItem);
        }

        return new Thickness(4 + depth * Indent, 0, 0, 0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
