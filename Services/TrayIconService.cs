using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;

namespace DynamicIsland.Services;

internal sealed class TrayIconService : IDisposable
{
    private const uint NifMessage = 0x1;
    private const uint NifIcon = 0x2;
    private const uint NifTip = 0x4;
    private const uint NimAdd = 0x0;
    private const uint NimDelete = 0x2;

    private readonly HwndSource _source;
    private readonly Action _onOpen;
    private readonly Action _onSettings;
    private readonly Action _onExit;
    private NativeMethods.NotifyIconData _data;
    private IntPtr _icon;
    private ContextMenu? _menu;
    private bool _disposed;

    public TrayIconService(Action onOpen, Action onSettings, Action onExit)
    {
        _onOpen = onOpen;
        _onSettings = onSettings;
        _onExit = onExit;
        _source = new HwndSource(new HwndSourceParameters("DynamicIslandTray")
        {
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3),
            Width = 0,
            Height = 0
        });
        _source.AddHook(Hook);
        _icon = LoadIcon();
        _data = new NativeMethods.NotifyIconData
        {
            Size = Marshal.SizeOf<NativeMethods.NotifyIconData>(),
            Window = _source.Handle,
            Id = 1,
            Flags = NifMessage | NifIcon | NifTip,
            CallbackMessage = NativeMethods.WmTray,
            Icon = _icon,
            Tip = "Dynamic Island"
        };
        NativeMethods.Shell_NotifyIcon(NimAdd, ref _data);
    }

    private static IntPtr LoadIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path) &&
                NativeMethods.ExtractIconEx(path, 0, out var large, out var small, 1) > 0)
            {
                var result = small != IntPtr.Zero ? small : large;
                if (large != IntPtr.Zero && large != result) NativeMethods.DestroyIcon(large);
                if (small != IntPtr.Zero && small != result) NativeMethods.DestroyIcon(small);
                if (result != IntPtr.Zero) return result;
            }
        }
        catch { }
        return IntPtr.Zero;
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WmTray) return IntPtr.Zero;
        var action = (int)lParam;
        if (action is NativeMethods.WmLButtonUp or NativeMethods.WmLButtonDblClk)
        {
            _onOpen();
            handled = true;
        }
        else if (action == NativeMethods.WmRButtonUp)
        {
            ShowMenu();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        if (_menu is null)
        {
            _menu = new ContextMenu
            {
                Style = (Style)Application.Current.Resources["TrayMenuStyle"],
                Items =
                {
                    Item("Открыть панель", _onOpen),
                    Item("Настройки", _onSettings),
                    new Separator { Style = (Style)Application.Current.Resources["TraySeparatorStyle"] },
                    Item("Выход", _onExit)
                }
            };
        }
        _menu.Placement = PlacementMode.MousePoint;
        _menu.IsOpen = true;
    }

    private static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header, Style = (Style)Application.Current.Resources["TrayMenuItemStyle"] };
        item.Click += (_, _) => action();
        return item;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NativeMethods.Shell_NotifyIcon(NimDelete, ref _data);
        if (_menu is not null) _menu.IsOpen = false;
        _source.RemoveHook(Hook);
        _source.Dispose();
        if (_icon != IntPtr.Zero) NativeMethods.DestroyIcon(_icon);
        _icon = IntPtr.Zero;
    }
}
