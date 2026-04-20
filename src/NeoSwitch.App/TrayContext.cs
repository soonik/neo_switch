namespace NeoSwitch.App;

/// <summary>
/// Hosts the tray icon + main form, owns the <see cref="RuntimeController"/>,
/// and controls the application lifetime. Exit is always via the tray menu.
/// </summary>
public sealed class TrayContext : ApplicationContext
{
    private readonly SettingsStore _store;
    private readonly RuntimeController _runtime;
    private readonly MainForm _form;
    private readonly NotifyIcon _tray;
    private readonly SynchronizationContext _ui;

    private readonly ToolStripMenuItem _miOpen;
    private readonly ToolStripMenuItem _miPause;
    private readonly ToolStripMenuItem _miReconnect;
    private readonly ToolStripMenuItem _miExit;

    public TrayContext()
    {
        // Captured on the UI thread — posts back to the WinForms message pump.
        _ui = SynchronizationContext.Current
              ?? throw new InvalidOperationException(
                  "TrayContext must be constructed on the WinForms UI thread.");

        _store   = new SettingsStore();
        _runtime = new RuntimeController(_store.Current);
        _form    = new MainForm(_runtime, _store, _ui);

        _miOpen      = new ToolStripMenuItem("Open NeoSwitch");
        _miPause     = new ToolStripMenuItem("Pause switching") { CheckOnClick = true };
        _miReconnect = new ToolStripMenuItem("Reconnect keyboard");
        _miExit      = new ToolStripMenuItem("Exit");

        var menu = new ContextMenuStrip();
        menu.Items.Add(_miOpen);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_miPause);
        menu.Items.Add(_miReconnect);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_miExit);

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "NeoSwitch",
            Visible = true,
            ContextMenuStrip = menu,
        };

        // Wire tray commands.
        _miOpen.Click      += (_, _) => _form.ShowAndActivate();
        _miPause.Click     += (_, _) =>
        {
            _runtime.TogglePause();
            _store.Save();
        };
        _miReconnect.Click += (_, _) => _runtime.Reconnect();
        _miExit.Click      += (_, _) => ExitThread();

        _tray.DoubleClick += (_, _) => _form.ShowAndActivate();

        _runtime.StateChanged += OnStateChanged;

        // Start the pipeline (connects the keyboard, installs the hook).
        _runtime.Start();

        // Update menu / tooltip to reflect initial state.
        OnStateChanged();
    }

    private void OnStateChanged()
    {
        _ui.Post(_ =>
        {
            _miPause.Checked = _store.Current.Paused;

            string state = _runtime.State switch
            {
                RuntimeState.Connected    => "Connected",
                RuntimeState.Connecting   => "Connecting…",
                RuntimeState.Paused       => "Paused",
                RuntimeState.Error        => "Error",
                _                         => "Disconnected",
            };
            string product = _runtime.ConnectedProduct ?? "no keyboard";
            string profile = _runtime.CurrentProfileIdx is byte idx ? $"profile {idx}" : "";
            string tooltip = $"NeoSwitch — {product}  ·  {state}{(profile.Length > 0 ? "  ·  " + profile : "")}";
            if (tooltip.Length > 63) tooltip = tooltip[..63];  // NotifyIcon.Text hard limit
            _tray.Text = tooltip;
        }, null);
    }

    protected override void ExitThreadCore()
    {
        try { _runtime.Dispose(); } catch { }
        try { _store.Save(); } catch { }
        try { _tray.Visible = false; _tray.Dispose(); } catch { }
        try { _form.Dispose(); } catch { }
        base.ExitThreadCore();
    }
}
