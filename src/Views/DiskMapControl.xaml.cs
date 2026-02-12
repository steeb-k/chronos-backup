using Chronos.Common.Extensions;
using Chronos.Core.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System.Collections.Generic;
using System.Linq;

namespace Chronos.App.Views;

/// <summary>
/// A Disk Management–style partition map control that displays partitions as proportional
/// horizontal blocks with label, filesystem, size, and usage bar rows.
/// </summary>
public sealed partial class DiskMapControl : UserControl
{
    // ---------- Dependency Properties ----------

    public static readonly DependencyProperty DiskProperty =
        DependencyProperty.Register(nameof(Disk), typeof(DiskInfo), typeof(DiskMapControl),
            new PropertyMetadata(null, OnDataChanged));

    public static readonly DependencyProperty PartitionsProperty =
        DependencyProperty.Register(nameof(Partitions), typeof(IList<PartitionInfo>), typeof(DiskMapControl),
            new PropertyMetadata(null, OnDataChanged));

    public static readonly DependencyProperty HighlightedPartitionProperty =
        DependencyProperty.Register(nameof(HighlightedPartition), typeof(PartitionInfo), typeof(DiskMapControl),
            new PropertyMetadata(null, OnDataChanged));

    public DiskInfo? Disk
    {
        get => (DiskInfo?)GetValue(DiskProperty);
        set => SetValue(DiskProperty, value);
    }

    public IList<PartitionInfo>? Partitions
    {
        get => (IList<PartitionInfo>?)GetValue(PartitionsProperty);
        set => SetValue(PartitionsProperty, value);
    }

    public PartitionInfo? HighlightedPartition
    {
        get => (PartitionInfo?)GetValue(HighlightedPartitionProperty);
        set => SetValue(HighlightedPartitionProperty, value);
    }

    // ---------- Color palette for partitions ----------

    private static readonly Windows.UI.Color[] PartitionColors = new[]
    {
        Windows.UI.Color.FromArgb(255, 90, 160, 255),   // Blue (Primary / data)
        Windows.UI.Color.FromArgb(255, 130, 210, 80),    // Green (EFI / system)
        Windows.UI.Color.FromArgb(255, 230, 150, 60),    // Orange (Recovery)
        Windows.UI.Color.FromArgb(255, 180, 120, 230),   // Purple
        Windows.UI.Color.FromArgb(255, 80, 200, 200),    // Teal
        Windows.UI.Color.FromArgb(255, 230, 90, 110),    // Red
    };

    public DiskMapControl()
    {
        this.InitializeComponent();
    }

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DiskMapControl control)
            control.Rebuild();
    }

    private void Rebuild()
    {
        PartitionGrid.Children.Clear();
        PartitionGrid.ColumnDefinitions.Clear();

        var disk = Disk;
        var parts = Partitions;

        // Header text
        if (disk is not null)
        {
            var style = disk.PartitionStyle switch
            {
                DiskPartitionStyle.GPT => "GPT",
                DiskPartitionStyle.MBR => "MBR",
                _ => "Unknown"
            };
            HeaderText.Text = $"{style}  •  {disk.Model}  •  {disk.SerialNumber}";
            HeaderText.Visibility = Visibility.Visible;
        }
        else
        {
            HeaderText.Visibility = Visibility.Collapsed;
        }

        if (parts is null || parts.Count == 0)
        {
            EmptyText.Visibility = Visibility.Visible;
            return;
        }

        EmptyText.Visibility = Visibility.Collapsed;

        // Calculate total size for proportional widths
        var totalSize = (double)parts.Sum(p => (long)p.Size);
        if (totalSize <= 0)
            totalSize = 1;

        // Minimum column width fraction so tiny partitions are still visible & readable
        const double minFraction = 0.06;

        // Compute raw fractions and enforce minimum
        var fractions = parts.Select(p => Math.Max((double)p.Size / totalSize, minFraction)).ToList();
        var fracSum = fractions.Sum();
        // Normalize so they sum to 1.0
        fractions = fractions.Select(f => f / fracSum).ToList();

        for (int i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            var fraction = fractions[i];

            // Column def uses star sizing for proportional width
            PartitionGrid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(fraction, GridUnitType.Star)
            });

            var highlighted = HighlightedPartition;
            bool isDimmed = highlighted is not null && highlighted != part;
            bool isHighlighted = highlighted is not null && highlighted == part;

            var block = BuildPartitionBlock(part, i, isDimmed, isHighlighted);
            Grid.SetColumn(block, i);
            PartitionGrid.Children.Add(block);
        }
    }

    private static FrameworkElement BuildPartitionBlock(PartitionInfo part, int index, bool isDimmed, bool isHighlighted)
    {
        var color = PickColor(part, index);
        var accentBrush = new SolidColorBrush(color);
        var dimBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(60, color.R, color.G, color.B));

        // Each partition block is a vertical stack with 4 rows:
        // 1. Label (type + name + letter)
        // 2. Filesystem
        // 3. Size
        // 4. Usage bar

        // Build tooltip with full details
        var sizeText = ((long)part.Size).ToHumanReadableSize();
        var tooltipText = $"{part.DisplayLabel}\n{part.FileSystem ?? "Unknown FS"}\n{sizeText}";
        if (part.UsedSpace.HasValue)
            tooltipText += $"\nUsed: {((long)part.UsedSpace.Value).ToHumanReadableSize()}";
        if (part.FreeSpace.HasValue)
            tooltipText += $"\nFree: {((long)part.FreeSpace.Value).ToHumanReadableSize()}";

        var highlightColor = Windows.UI.Color.FromArgb(255, 60, 140, 255); // Bright blue

        var stack = new StackPanel
        {
            Spacing = 1,
            Padding = new Thickness(6, 5, 6, 5),
            BorderBrush = isHighlighted
                ? new SolidColorBrush(highlightColor)
                : new SolidColorBrush(Windows.UI.Color.FromArgb(40, 255, 255, 255)),
            BorderThickness = isHighlighted ? new Thickness(2) : new Thickness(0, 0, 1, 0),
            Opacity = isDimmed ? 0.3 : 1.0,
        };
        ToolTipService.SetToolTip(stack, tooltipText);

        // Row 1: Display label
        stack.Children.Add(new TextBlock
        {
            Text = part.DisplayLabel,
            FontSize = 11.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = accentBrush,
        });

        // Row 2: Filesystem
        stack.Children.Add(new TextBlock
        {
            Text = part.FileSystem ?? "—",
            FontSize = 11,
            Opacity = 0.6,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        // Row 3: Size
        stack.Children.Add(new TextBlock
        {
            Text = sizeText,
            FontSize = 11,
            Opacity = 0.6,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        // Row 4: Usage bar
        var usageBar = BuildUsageBar(part, accentBrush, dimBrush);
        stack.Children.Add(usageBar);

        return stack;
    }

    private static FrameworkElement BuildUsageBar(PartitionInfo part, SolidColorBrush accentBrush, SolidColorBrush dimBrush)
    {
        var ratio = part.UsageRatio;
        // Light gray background works in both dark and light themes
        var barBackground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 180, 180, 180));
        var container = new Grid
        {
            Height = 5,
            CornerRadius = new CornerRadius(2),
        };

        if (ratio.HasValue)
        {
            // Background (total capacity)
            container.Children.Add(new Border
            {
                Background = barBackground,
                CornerRadius = new CornerRadius(2),
            });

            // Foreground (used portion)
            container.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(Math.Max(ratio.Value, 0.01), GridUnitType.Star)
            });
            container.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1.0 - ratio.Value, GridUnitType.Star)
            });

            var usedBar = new Border
            {
                Background = accentBrush,
                CornerRadius = new CornerRadius(2, 0, 0, 2),
                Opacity = 0.85,
            };
            Grid.SetColumn(usedBar, 0);
            container.Children.Add(usedBar);
        }
        else
        {
            // No usage data – show a thin placeholder
            container.Children.Add(new Border
            {
                Background = barBackground,
                CornerRadius = new CornerRadius(2),
                Opacity = 0.5,
            });
        }

        // Wrap in a border that provides a visible outline
        var outline = new Border
        {
            Margin = new Thickness(0, 3, 0, 0),
            CornerRadius = new CornerRadius(2),
            BorderThickness = new Thickness(1.5),
            Child = container,
        };
        // Pick a border color with real contrast against the partition block background
        var isDark = Application.Current.RequestedTheme == ApplicationTheme.Dark;
        var borderColor = isDark
            ? Windows.UI.Color.FromArgb(255, 160, 160, 160)   // light gray on dark bg
            : Windows.UI.Color.FromArgb(255, 80, 80, 80);     // dark gray on light bg
        outline.BorderBrush = new SolidColorBrush(borderColor);

        return outline;
    }

    private static Windows.UI.Color PickColor(PartitionInfo part, int index)
    {
        // Unallocated space gets a neutral dark gray
        if (part.IsUnallocated)
            return Windows.UI.Color.FromArgb(255, 100, 100, 100);

        // Use semantic colors based on partition type
        var type = part.PartitionType?.ToUpperInvariant() ?? string.Empty;
        if (type.Contains("EFI") || type.Contains("ESP") || type.Contains("SYSTEM"))
            return PartitionColors[1]; // Green
        if (type.Contains("RECOVERY"))
            return PartitionColors[2]; // Orange
        if (type.Contains("MSR") || type.Contains("RESERVED"))
            return PartitionColors[3]; // Purple

        // Default: cycle through blue, teal, etc. for data partitions
        var dataColors = new[] { PartitionColors[0], PartitionColors[4], PartitionColors[5] };
        return dataColors[index % dataColors.Length];
    }
}
