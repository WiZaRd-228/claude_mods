using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace ClaudeLimitsTray;

internal enum SessionState
{
    Starting,
    Ok,
    NeedsSession,
    Challenge,
    Error,
}

internal sealed class TrayApp : ApplicationContext
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "ClaudeLimitsTray";
    private const int TooltipLimit = 127;
    private static readonly int[] WarnThresholds = { 80, 95 };

    private readonly Settings settings = Settings.Load();
    private readonly ClaudeWeb web = new();
    private readonly TrayIconRenderer icons = new();
    private readonly NotifyIcon tray = new() { Text = "Claude limits: starting…" };
    private readonly ContextMenuStrip menu = new();
    private readonly System.Windows.Forms.Timer startTimer = new() { Interval = 100 };
    private readonly System.Windows.Forms.Timer pollTimer = new() { Interval = 60_000 };
    private readonly System.Windows.Forms.Timer tickTimer = new() { Interval = 30_000 };
    private readonly OverlayForm overlay = new();
    private readonly ClaudeWindowTracker tracker = new();
    private readonly InputBoxTracker inputTracker = new();

    private List<LimitWindow> windows = new();
    private SessionState state = SessionState.Starting;
    private string? statusMessage = "Starting…";
    private bool isWebReady;
    private bool isRefreshing;
    private bool isShapeLogged;
    private bool isSignInHintShown;

    public TrayApp()
    {
        tray.Icon = icons.RenderIdle("…");
        tray.ContextMenuStrip = menu;
        tray.Visible = true;
        tray.MouseClick += async (_, e) =>
        {
            if (e.Button == MouseButtons.Left) await RefreshAsync();
        };

        menu.Opening += (_, _) => BuildMenu();
        startTimer.Tick += async (_, _) =>
        {
            startTimer.Stop();
            await StartAsync();
        };
        pollTimer.Tick += async (_, _) => await RefreshAsync();
        tickTimer.Tick += (_, _) => UpdateViews();
        tracker.Changed += () =>
        {
            inputTracker.Update(tracker.ClaudeWindow, tracker.IsClaudeActive);
            PlaceOverlay();
        };
        inputTracker.Changed += PlaceOverlay;
        inputTracker.IsEnabled = settings.FollowInput;
        inputTracker.Update(tracker.ClaudeWindow, tracker.IsClaudeActive);
        overlay.Dragged += SaveOverlayOffset;

        UpdateViews();
        startTimer.Start();
    }

    private async Task StartAsync()
    {
        try
        {
            await web.InitializeAsync();
            isWebReady = true;
        }
        catch (Exception ex)
        {
            Log.Write($"WebView2 init failed: {ex}");
            SetState(SessionState.Error, "WebView2 Runtime missing: install it from microsoft.com/edge/webview2");
            return;
        }

        if (!await web.HasSessionAsync()) TryImport(notify: false);
        await RefreshAsync();
        pollTimer.Start();
        tickTimer.Start();
    }

    private bool TryImport(bool notify)
    {
        var result = DesktopSession.Import();
        Log.Write($"Session import: {result.Status}{(result.Error is null ? "" : " - " + result.Error)}");
        if (result.Status == ImportStatus.Ok && result.SessionKey is not null)
        {
            web.SetSession(result.SessionKey, result.Organization);
            return true;
        }

        if (notify) ShowBalloon("Session not imported", ImportHint(result.Status), ToolTipIcon.Warning);
        return false;
    }

    private static string ImportHint(ImportStatus status) => status switch
    {
        ImportStatus.Locked => "Quit Claude Desktop once (tray icon > Quit), then import again. Or use Sign in.",
        ImportStatus.NotFound => "No Claude Desktop session found. Use Sign in.",
        _ => "Import failed (see log). Use Sign in.",
    };

    private async Task RefreshAsync()
    {
        if (!isWebReady || isRefreshing) return;
        isRefreshing = true;
        try
        {
            if (state == SessionState.NeedsSession && !TryImport(notify: false))
            {
                UpdateViews();
                return;
            }

            var result = await web.FetchUsageAsync();
            switch (result.Outcome)
            {
                case FetchOutcome.Ok:
                    ApplyUsage(result.Body ?? "");
                    break;
                case FetchOutcome.SignedOut:
                    SetState(SessionState.NeedsSession, "Sign-in needed");
                    if (!isSignInHintShown)
                    {
                        ShowBalloon("Claude limits needs your session", "Quit Claude Desktop once so the session can be imported, or right-click > Sign in.", ToolTipIcon.Info);
                        isSignInHintShown = true;
                    }

                    break;
                case FetchOutcome.Challenge:
                    SetState(SessionState.Challenge, "Cloudflare check: right-click > Open claude.ai");
                    break;
                default:
                    Log.Write($"Fetch failed: {result.Error} (HTTP {result.Status})");
                    SetState(SessionState.Error, result.Error ?? "Request failed");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Refresh error: {ex}");
            SetState(SessionState.Error, "Request failed, retrying");
        }
        finally
        {
            isRefreshing = false;
        }
    }

    private void ApplyUsage(string body)
    {
        if (!isShapeLogged)
        {
            Log.Write($"Usage response: {Usage.Shape(body)}");
            isShapeLogged = true;
        }

        List<LimitWindow> parsed;
        try
        {
            parsed = Usage.Parse(body);
        }
        catch (JsonException)
        {
            parsed = new List<LimitWindow>();
        }

        if (parsed.Count == 0)
        {
            Log.Write($"Unrecognized usage response: {Usage.Shape(body)}");
            SetState(SessionState.Error, "Unknown response format (see log)");
            return;
        }

        NotifyThresholds(parsed);
        windows = parsed;
        isSignInHintShown = false;
        SetState(SessionState.Ok, null);
    }

    private void NotifyThresholds(List<LimitWindow> fresh)
    {
        var warned = new Dictionary<string, int>();
        foreach (var w in fresh)
        {
            var id = $"{w.Kind}:{w.ResetsAt:O}";
            var crossed = WarnThresholds.Where(t => w.Percent >= t).DefaultIfEmpty(0).Max();
            settings.Warned.TryGetValue(id, out var before);
            warned[id] = Math.Max(before, crossed);
            if (crossed > before)
            {
                var detail = w.Left.Length > 0 ? $"Resets in {w.Left}" : "Limit almost used";
                ShowBalloon($"Claude {w.Label} limit: {w.Percent:0.#}%", detail, crossed >= 95 ? ToolTipIcon.Warning : ToolTipIcon.Info);
            }

            var old = windows.FirstOrDefault(o => o.Kind == w.Kind);
            if (old is not null && old.Percent >= WarnThresholds[0] && old.ResetsAt != w.ResetsAt && w.Percent < old.Percent)
            {
                ShowBalloon($"Claude {w.Label} limit reset", $"Now at {w.Percent:0.#}%", ToolTipIcon.Info);
            }
        }

        if (!warned.OrderBy(p => p.Key).SequenceEqual(settings.Warned.OrderBy(p => p.Key)))
        {
            settings.Warned = warned;
            settings.Save();
        }
    }

    private void SetState(SessionState newState, string? message)
    {
        state = newState;
        statusMessage = message;
        UpdateViews();
    }

    private void UpdateViews()
    {
        var main = windows.FirstOrDefault(w => w.Kind == "five_hour") ?? windows.FirstOrDefault();
        if (state == SessionState.Ok && main is not null)
        {
            var percent = main.ShownPercent;
            tray.Icon = icons.Render(((int)Math.Round(percent)).ToString(), Usage.ColorOf(percent));
        }
        else
        {
            tray.Icon = icons.RenderIdle(state switch
            {
                SessionState.Starting => "…",
                SessionState.NeedsSession => "?",
                _ => "!",
            });
        }

        tray.Text = Tooltip();
        overlay.SetContent(windows, statusMessage);
        PlaceOverlay();
    }

    private string Tooltip()
    {
        var lines = new List<string> { "Claude limits" };
        lines.AddRange(windows.Select(w => $"{w.Label}: {w.ShownPercent:0.#}%{(w.Left.Length > 0 ? " · " + w.Left : "")}"));
        if (statusMessage is not null) lines.Add(statusMessage);
        var text = string.Join("\n", lines);
        return text.Length > TooltipLimit ? text[..TooltipLimit] : text;
    }

    private void PlaceOverlay()
    {
        var bounds = tracker.ClaudeBounds;
        var isVisible = settings.Overlay switch
        {
            OverlayMode.Off => false,
            OverlayMode.Always => true,
            _ => tracker.IsClaudeActive && bounds is not null,
        };

        if (!isVisible)
        {
            if (overlay.Visible) overlay.Hide();
            return;
        }

        if (InputBounds(bounds) is { } input)
        {
            overlay.SetWidth(input.Width);
            var x = input.Left + (input.Width - overlay.Width) / 2 + settings.InputOffsetX;
            var y = input.Top - settings.InputGap - overlay.Height;
            overlay.Location = ClampToScreen(new Point(x, y), overlay.Size);
        }
        else if (bounds is { } b)
        {
            overlay.SetWidth(Math.Min(overlay.DefaultDeviceWidth, b.Width - 40));
            var x = b.Left + (b.Width - overlay.Width) / 2 + settings.OverlayOffsetX;
            var y = b.Bottom - settings.OverlayOffsetY - overlay.Height;
            overlay.Location = ClampToScreen(new Point(x, y), overlay.Size);
        }
        else if (!overlay.Visible)
        {
            overlay.SetWidth(overlay.DefaultDeviceWidth);
            var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
            overlay.Location = new Point(area.Right - overlay.Width - 16, area.Bottom - overlay.Height - 16);
        }

        if (!overlay.Visible) overlay.Show();
    }

    private static Point ClampToScreen(Point location, Size size)
    {
        var area = Screen.FromPoint(location).WorkingArea;
        var x = Math.Clamp(location.X, area.Left, Math.Max(area.Left, area.Right - size.Width));
        var y = Math.Clamp(location.Y, area.Top, Math.Max(area.Top, area.Bottom - size.Height));
        return new Point(x, y);
    }

    private Rectangle? InputBounds(Rectangle? window) =>
        settings.FollowInput && window is { } w ? inputTracker.BoundsIn(w) : null;

    private void SaveOverlayOffset(Point location)
    {
        var bounds = tracker.ClaudeBounds;
        if (InputBounds(bounds) is { } input)
        {
            settings.InputOffsetX = location.X + overlay.Width / 2 - (input.Left + input.Width / 2);
            settings.InputGap = input.Top - (location.Y + overlay.Height);
        }
        else if (bounds is { } b)
        {
            settings.OverlayOffsetX = location.X + overlay.Width / 2 - (b.Left + b.Width / 2);
            settings.OverlayOffsetY = b.Bottom - (location.Y + overlay.Height);
        }
        else
        {
            return;
        }

        settings.Save();
    }

    private void BuildMenu()
    {
        menu.Items.Clear();
        if (windows.Count == 0) menu.Items.Add(new ToolStripMenuItem(statusMessage ?? "No data yet") { Enabled = false });
        foreach (var w in windows)
        {
            var left = w.IsReset ? "  ·  reset" : w.Left.Length > 0 ? $"  ·  resets in {w.Left}" : "";
            menu.Items.Add(new ToolStripMenuItem($"{w.Label}: {w.ShownPercent:0.#}%{left}") { Enabled = false });
        }

        if (windows.Count > 0 && statusMessage is not null) menu.Items.Add(new ToolStripMenuItem(statusMessage) { Enabled = false });

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Refresh now", null, async (_, _) => await RefreshAsync());

        var overlayMenu = new ToolStripMenuItem("Overlay");
        overlayMenu.DropDownItems.Add(ModeItem("Show when Claude is active", OverlayMode.WhenClaudeActive));
        overlayMenu.DropDownItems.Add(ModeItem("Always show", OverlayMode.Always));
        overlayMenu.DropDownItems.Add(ModeItem("Hide", OverlayMode.Off));
        overlayMenu.DropDownItems.Add(new ToolStripSeparator());
        overlayMenu.DropDownItems.Add(new ToolStripMenuItem("Follow the message box", null, (_, _) =>
        {
            settings.FollowInput = !settings.FollowInput;
            inputTracker.IsEnabled = settings.FollowInput;
            settings.Save();
            PlaceOverlay();
        })
        {
            Checked = settings.FollowInput,
        });
        overlayMenu.DropDownItems.Add("Reset position", null, (_, _) =>
        {
            var defaults = new Settings();
            settings.OverlayOffsetX = defaults.OverlayOffsetX;
            settings.OverlayOffsetY = defaults.OverlayOffsetY;
            settings.InputOffsetX = defaults.InputOffsetX;
            settings.InputGap = defaults.InputGap;
            settings.Save();
            PlaceOverlay();
        });
        menu.Items.Add(overlayMenu);
        menu.Items.Add(new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleAutostart()) { Checked = IsAutostart() });

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Import session from Claude Desktop", null, async (_, _) =>
        {
            if (TryImport(notify: true)) await RefreshAsync();
        });
        menu.Items.Add("Sign in to claude.ai…", null, async (_, _) => await SignInAsync());
        if (state == SessionState.Challenge) menu.Items.Add("Open claude.ai (Cloudflare check)…", null, async (_, _) => await OpenClaudeAsync());
        menu.Items.Add("Open log folder", null, (_, _) => Process.Start("explorer.exe", AppPaths.Root));

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
    }

    private ToolStripMenuItem ModeItem(string text, OverlayMode mode) => new(text, null, (_, _) =>
    {
        settings.Overlay = mode;
        settings.Save();
        PlaceOverlay();
    })
    {
        Checked = settings.Overlay == mode,
    };

    private async Task SignInAsync()
    {
        if (!isWebReady) return;
        web.ClearSession();
        using var form = new SignInForm(web.Environment, "https://claude.ai/login", "Sign in to Claude - Claude Limits");
        if (form.ShowDialog() == DialogResult.OK)
        {
            state = SessionState.Starting;
            await RefreshAsync();
        }
    }

    private async Task OpenClaudeAsync()
    {
        if (!isWebReady) return;
        using var form = new SignInForm(web.Environment, "https://claude.ai/", "claude.ai - pass the check, then close this window")
        {
            StopOnSession = false,
        };
        form.ShowDialog();
        await RefreshAsync();
    }

    private static bool IsAutostart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string;
    }

    private static void ToggleAutostart()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (key.GetValue(RunValue) is string) key.DeleteValue(RunValue, throwOnMissingValue: false);
        else key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
    }

    private void ShowBalloon(string title, string text, ToolTipIcon icon) =>
        tray.ShowBalloonTip(6000, title, string.IsNullOrEmpty(text) ? " " : text, icon);

    protected override void ExitThreadCore()
    {
        tray.Visible = false;
        startTimer.Dispose();
        pollTimer.Dispose();
        tickTimer.Dispose();
        tracker.Dispose();
        inputTracker.Dispose();
        overlay.Dispose();
        web.Dispose();
        tray.Dispose();
        menu.Dispose();
        icons.Dispose();
        base.ExitThreadCore();
    }
}
