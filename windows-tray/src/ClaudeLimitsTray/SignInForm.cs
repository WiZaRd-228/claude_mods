using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ClaudeLimitsTray;

internal sealed class SignInForm : Form
{
    private readonly CoreWebView2Environment environment;
    private readonly string startUrl;
    private readonly WebView2 view = new() { Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer sessionCheck = new() { Interval = 1500 };

    public SignInForm(CoreWebView2Environment environment, string startUrl, string title)
    {
        this.environment = environment;
        this.startUrl = startUrl;
        Text = title;
        Size = new Size(520, 760);
        StartPosition = FormStartPosition.CenterScreen;
        ShowIcon = false;
        Controls.Add(view);
        sessionCheck.Tick += async (_, _) => await CheckSessionAsync();
    }

    public bool StopOnSession { get; init; } = true;

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        try
        {
            await view.EnsureCoreWebView2Async(environment);
            view.CoreWebView2.Navigate(startUrl);
            if (StopOnSession) sessionCheck.Start();
        }
        catch (Exception ex)
        {
            Log.Write($"Sign-in window failed: {ex.Message}");
            MessageBox.Show(this, $"Could not open the sign-in page: {ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private async Task CheckSessionAsync()
    {
        if (view.CoreWebView2 is null) return;
        var cookies = await view.CoreWebView2.CookieManager.GetCookiesAsync("https://claude.ai");
        if (!cookies.Any(c => c.Name == "sessionKey" && !string.IsNullOrEmpty(c.Value))) return;
        sessionCheck.Stop();
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            sessionCheck.Dispose();
            view.Dispose();
        }

        base.Dispose(disposing);
    }
}
