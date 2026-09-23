using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Media3D;
namespace KickoutMonitor.App.Services;
public static class ReviewKeyboard
{
    public static bool ShouldDispatch(bool isRepeat, System.Windows.Input.ModifierKeys modifiers) =>
        !isRepeat && modifiers == System.Windows.Input.ModifierKeys.None;
    public static bool IsEditor(IInputElement? focused)
    {
        for (var node = focused as DependencyObject; node is not null;
            node = node is Visual or Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node is TextBoxBase or ComboBox or DatePicker or PasswordBox) return true;
        return false;
    }
}
