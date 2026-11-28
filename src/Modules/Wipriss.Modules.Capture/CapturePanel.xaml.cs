using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wipriss.Core;
using Wipriss.Core.Input;
using Wipriss.Core.Modules;
using Wipriss.Core.Ui;

namespace Wipriss.Modules.Capture;

public partial class CapturePanel : UserControl
{
    private const string FreeKeys = "Чтобы всё работало, проверьте, что эти клавиши не заняты другой программой или действием.";

    private readonly CaptureModule _module;

    internal CapturePanel(CaptureModule module, IPanelContext panel)
    {
        InitializeComponent();
        _module = module;
        Loaded += (_, _) => Build();
    }

    private void Build()
    {
        var row = new HotkeyRow("Снимок экрана", _module.PrintScreen.Key, Hotkey.PrtSc, k => k.IsUsableForHook,
            "Подойдёт PrtSc, Pause, F1–F24 или сочетание с Ctrl, Alt или Win.")
        {
            AllowNone = false,
        };
        // PrtSc itself comes from the module's hook while recording (Windows would give it to Snipping Tool).
        row.Recording += on => _module.PauseKey(on, mods => Dispatcher.BeginInvoke(() => row.Offer(new Hotkey(mods, Hotkey.PrintScreen))));
        row.Chosen += key =>
        {
            try { _module.SetKey(key); }
            catch (Exception ex) { ErrorLog.Write(ex); }
            ShowState();
        };
        KeyHost.Content = row;
        ShowState();
    }

    private void ShowState()
    {
        HowText.Text = HotkeyRow.Pretty(_module.PrintScreen.Key) + " → выделите область, щёлкните по окну, «Весь экран» или «Со скроллом».";
        if (_module.HookActive)
            Show("OkBrush", FreeKeys);
        else if (_module.KeyRegistered)
            Show("WarnBrush", "Windows не дала перехватить клавишу сразу. Она заработает после следующего входа в Windows. " + FreeKeys);
        else
            Show("DangerBrush", "Клавиша занята другой программой, например Lightshot, Яндекс.Диском или OneDrive. Отключите её там или выберите другую.");
    }

    private void Show(string brush, string text)
    {
        StateDot.Fill = (Brush)FindResource(brush);
        StateText.Text = text;
    }
}
