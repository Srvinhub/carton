using Avalonia.Controls;
using Avalonia.Input;
using carton.ViewModels;

namespace carton.Views.Pages;

public partial class GroupsView : UserControl
{
    public GroupsView()
    {
        InitializeComponent();
    }

    private void OnProxySelectPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: OutboundItemViewModel item })
        {
            return;
        }

        if (item.SelectOutboundCommand == null || string.IsNullOrWhiteSpace(item.Tag))
        {
            return;
        }

        if (!item.SelectOutboundCommand.CanExecute(item.Tag))
        {
            e.Handled = true;
            return;
        }

        _ = item.SelectOutboundCommand.ExecuteAsync(item.Tag);
        e.Handled = true;
    }

    private void OnProxyPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: OutboundItemViewModel item })
        {
            item.IsHovered = true;
        }
    }

    private void OnProxyPointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: OutboundItemViewModel item })
        {
            item.IsHovered = false;
        }
    }
}
