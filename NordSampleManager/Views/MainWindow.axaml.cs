using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using NordSampleManager.Protocol.Records;
using NordSampleManager.Services;
using NordSampleManager.ViewModels;

namespace NordSampleManager.Views;

public partial class MainWindow : Window
{
    private static readonly DataFormat<BankEntry> DragFormat =
        DataFormat.CreateInProcessFormat<BankEntry>("nord.bankEntry");

    public MainWindow()
    {
        InitializeComponent();
        var grid = this.FindControl<DataGrid>("ProgramsGrid");
        grid?.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        grid?.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private async void OnGripPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var entry = (sender as Control)?.DataContext as BankEntry;
        if (entry?.Ref?.ItemType != SoundItemType.Program) return;

        var item = new DataTransferItem();
        item.Set(DragFormat, entry);
        var data = new DataTransfer();
        data.Add(item);

        await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DragFormat) ? DragDropEffects.Move : DragDropEffects.None;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var source = e.DataTransfer.TryGetValue(DragFormat);
        if (source is null) return;
        var grid = sender as DataGrid;
        if (grid is null) return;
        var target = FindRowAtPosition(grid, e.GetPosition(grid));
        if (target is null || ReferenceEquals(target, source)) return;
        if (target.Ref?.ItemType != SoundItemType.Program) return;
        if (DataContext is MainWindowViewModel vm)
            await vm.SwapAsync(source, target);
    }

    private static BankEntry? FindRowAtPosition(DataGrid grid, Avalonia.Point pos)
    {
        return grid.GetVisualDescendants()
            .OfType<DataGridRow>()
            .FirstOrDefault(row =>
            {
                var p = Avalonia.VisualExtensions.TranslatePoint(row, new Avalonia.Point(0, 0), grid);
                return p.HasValue && new Avalonia.Rect(p.Value, row.Bounds.Size).Contains(pos);
            })
            ?.DataContext as BankEntry;
    }
}
