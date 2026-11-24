using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Wipriss.Core.Modules;
using Wipriss.Core.Ui;

namespace Wipriss.App;

/// <summary>
/// The window a module's panel opens in, right under the island. It closes when you click elsewhere
/// or press Esc, and is thrown away on close so it costs nothing afterwards.
/// </summary>
public partial class FlyoutWindow : Window, IPanelContext
{
    private int _keepOpen;
    private bool _closing;

    public FlyoutWindow(IModule module)
    {
        InitializeComponent();
        ModuleId = module.Info.Id;
        Title = $"Wipriss · {module.Info.Title}";
        GlyphText.Text = module.Info.Glyph;
        TitleText.Text = module.Info.Title;
        Width = module.PanelSize.Width;
        Height = module.PanelSize.Height;
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 48;
        Host.Content = module.CreatePanel(this);
    }

    public string ModuleId { get; }

    public IDisposable KeepOpen()
    {
        _keepOpen++;
        return new Release(() => _keepOpen--);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeWindowStyle.RoundCorners(this);
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (_keepOpen == 0 && !_closing) Dispatcher.BeginInvoke(() => { if (!_closing && _keepOpen == 0) Close(); });
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _closing = true;
        base.OnClosing(e);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private sealed class Release(Action action) : IDisposable
    {
        private Action? _action = action;

        public void Dispose()
        {
            _action?.Invoke();
            _action = null;
        }
    }
}
