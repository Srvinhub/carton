using System;
using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using carton.ViewModels;

namespace carton.Views.Pages;

public partial class GroupsView : UserControl
{
    private const double ProxyToolTipMinWidth = 72;
    private const double ProxyToolTipMaxWidth = 280;
    private const double ProxyToolTipAverageCharWidth = 7;
    private const double ProxyToolTipHorizontalPadding = 24;
    private const double ProxyToolTipOffset = 12;
    private const double ProxyCardMinWidth = 200;
    private const double ProxyCardGap = 8;
    private const double ProxyCardHorizontalReserve = 16;
    private const int ProxyCardMinColumns = 2;

    public GroupsView()
    {
        InitializeComponent();
    }

    private void OnGroupItemPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: GroupItemViewModel group } ||
            DataContext is not GroupsViewModel viewModel)
        {
            return;
        }

        if (e.Source is Visual sourceVisual && HasToggleExclusionAncestor(sourceVisual, sender as Visual))
        {
            return;
        }

        if (!viewModel.ToggleGroupExpansionCommand.CanExecute(group))
        {
            return;
        }

        viewModel.ToggleGroupExpansionCommand.Execute(group);
        e.Handled = true;
    }

    private static bool HasToggleExclusionAncestor(Visual sourceVisual, Visual? groupRoot)
    {
        for (Visual? current = sourceVisual; current != null && !ReferenceEquals(current, groupRoot); current = current.GetVisualParent() as Visual)
        {
            if (current is Button || current is Control { DataContext: OutboundItemViewModel })
            {
                return true;
            }
        }

        return false;
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

    private void OnGroupTestDelayPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: GroupItemViewModel group } ||
            DataContext is not GroupsViewModel viewModel)
        {
            return;
        }

        if (viewModel.TestGroupCardCommand.CanExecute(group))
        {
            viewModel.TestGroupCardCommand.Execute(group);
        }

        e.Handled = true;
    }

    private void OnProxyTestDelayPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: OutboundItemViewModel item })
        {
            return;
        }

        if (item.TestDelayCommand != null && item.TestDelayCommand.CanExecute(null))
        {
            item.TestDelayCommand.Execute(null);
        }

        e.Handled = true;
    }

    private void OnProxySelectSurfacePointerEntered(object? sender, PointerEventArgs e)
    {
        UpdateProxyToolTipPlacement(sender);
        SetProxyItemHoveredClass(sender, isHovered: true);
    }

    private void OnProxySelectSurfacePointerExited(object? sender, PointerEventArgs e)
    {
        SetProxyItemHoveredClass(sender, isHovered: false);
    }

    private static void SetProxyItemHoveredClass(object? sender, bool isHovered)
    {
        if (!TryGetProxyItemBorder(sender, out var border))
        {
            return;
        }

        border.Classes.Set("hovered", isHovered);
    }

    private static bool TryGetProxyItemBorder(object? sender, [NotNullWhen(true)] out Border? border)
    {
        if (sender is not Visual sourceVisual)
        {
            border = null;
            return false;
        }

        for (Visual? current = sourceVisual; current != null; current = current.GetVisualParent() as Visual)
        {
            if (current is Border { Name: "ProxyItemBorder" } candidate)
            {
                border = candidate;
                return true;
            }
        }

        border = null;
        return false;
    }

    private static void UpdateProxyToolTipPlacement(object? sender)
    {
        if (sender is not Control { DataContext: OutboundItemViewModel item } ||
            !TryGetProxyItemBorder(sender, out var border) || border == null)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(border);
        if (topLevel == null)
        {
            return;
        }

        var topLeft = border.TranslatePoint(new Point(0, 0), topLevel);
        if (topLeft == null)
        {
            return;
        }

        var preferredWidth = EstimateProxyToolTipWidth(item);
        var availableRight = topLevel.Bounds.Width - (topLeft.Value.X + border.Bounds.Width);
        var availableLeft = topLeft.Value.X;
        var requiredWidth = preferredWidth + ProxyToolTipOffset;
        var placeLeft = availableRight < requiredWidth && availableLeft > availableRight;

        ToolTip.SetPlacement(border, placeLeft ? PlacementMode.Left : PlacementMode.Right);
        ToolTip.SetHorizontalOffset(border, placeLeft ? -ProxyToolTipOffset : ProxyToolTipOffset);
    }

    private static double EstimateProxyToolTipWidth(OutboundItemViewModel item)
    {
        if (string.IsNullOrWhiteSpace(item.Tag))
        {
            return ProxyToolTipMinWidth;
        }

        var estimatedWidth = item.Tag.Length * ProxyToolTipAverageCharWidth + ProxyToolTipHorizontalPadding;
        return Math.Clamp(estimatedWidth, ProxyToolTipMinWidth, ProxyToolTipMaxWidth);
    }

    private void OnExpandedProxiesListSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is not Control { DataContext: GroupItemViewModel group })
        {
            return;
        }

        var width = e.NewSize.Width;
        if (double.IsNaN(width) || width <= 0)
        {
            return;
        }

        var availableWidth = Math.Max(0, width - ProxyCardHorizontalReserve);

        var columns = Math.Max(
            ProxyCardMinColumns,
            (int)Math.Floor((availableWidth + ProxyCardGap) / (ProxyCardMinWidth + ProxyCardGap)));

        group.SetExpandedProxyColumns(columns);
    }

}
