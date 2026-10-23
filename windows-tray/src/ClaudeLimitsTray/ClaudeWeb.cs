using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ClaudeLimitsTray;

internal enum FetchOutcome
{
    Ok,
    SignedOut,
    Challenge,
    Failed,
}

internal sealed record FetchResult(FetchOutcome Outcome, string? Body = null, int Status = 0, string? Error = null);

internal sealed class ClaudeWeb : IDisposable
{
    private const string Origin = "https://claude.ai";
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ChallengeWait = TimeSpan.FromSeconds(20);

    private readonly Form host = new()
    {
        ShowInTaskbar = false,
        FormBorderStyle = FormBorderStyle.None,
        StartPosition = FormStartPosition.Manual,
        Location = new Point(-32000, -32000),
        Size = new Size(900, 700),
        Opacity = 0,
    };

    private readonly WebView2 view = new() { Dock = DockStyle.Fill };
    private CoreWebView2Environment? environment;
    private bool isBusy;
    private string? organization;

    public CoreWebView2Environment Environment => environment ?? throw new InvalidOperationException("Not initialized");

    public async Task InitializeAsync()
    {
        environment = await CoreWebView2Environment.CreateAsync(null, AppPaths.WebProfile);
        host.Controls.Add(view);
        host.Show();
        host.Hide();
        await view.EnsureCoreWebView2Async(environment);
        var settings = view.CoreWebView2.Settings;
        settings.AreDevToolsEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
    }

    public async Task<bool> HasSessionAsync()
    {
        var cookies = await view.CoreWebView2.CookieManager.GetCookiesAsync(Origin);
        return cookies.Any(c => c.Name == "sessionKey" && !string.IsNullOrEmpty(c.Value));
    }

    public void SetSession(string sessionKey, string? organizationId)
    {
        var manager = view.CoreWebView2.CookieManager;
        var cookie = manager.CreateCookie("sessionKey", sessionKey, ".claude.ai", "/");
        cookie.IsSecure = true;
        cookie.IsHttpOnly = true;
        cookie.SameSite = CoreWebView2CookieSameSiteKind.Lax;
        cookie.Expires = DateTime.Now.AddDays(28);
        manager.AddOrUpdateCookie(cookie);
        if (!string.IsNullOrEmpty(organizationId)) organization = organizationId;
    }

    public void ClearSession()
    {
        view.CoreWebView2.CookieManager.DeleteAllCookies();
        organization = null;
    }

    public async Task<FetchResult> FetchUsageAsync()
    {
        if (isBusy) return new FetchResult(FetchOutcome.Failed, Error: "Previous request still running");
        isBusy = true;
        try
        {
            organization ??= await ResolveOrganizationAsync();
            if (organization is null) return new FetchResult(FetchOutcome.SignedOut, Error: "No organization for this session");

            var result = await GetJsonAsync($"{Origin}/api/organizations/{organization}/usage");
            if (result.Outcome == FetchOutcome.SignedOut) organization = null;
            return result;
        }
        finally
        {
            isBusy = false;
        }
    }

    private async Task<string?> ResolveOrganizationAsync()
    {
        var cookies = await view.CoreWebView2.CookieManager.GetCookiesAsync(Origin);
        var fromCookie = cookies.FirstOrDefault(c => c.Name == "lastActiveOrg")?.Value;
        if (!string.IsNullOrEmpty(fromCookie)) return fromCookie;

        var result = await GetJsonAsync($"{Origin}/api/organizations");
        if (result.Outcome != FetchOutcome.Ok || result.Body is null) return null;
        using var doc = JsonDocument.Parse(result.Body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
        foreach (var org in doc.RootElement.EnumerateArray())
        {
            if (org.TryGetProperty("uuid", out var uuid) && uuid.ValueKind == JsonValueKind.String) return uuid.GetString();
        }

        return null;
    }

    private async Task<FetchResult> GetJsonAsync(string url)
    {
        var core = view.CoreWebView2;
        var completed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e) => completed.TrySetResult(e.HttpStatusCode);

        core.NavigationCompleted += OnCompleted;
        try
        {
            core.Navigate(url);
            var finished = await Task.WhenAny(completed.Task, Task.Delay(NavigationTimeout));
            if (finished != completed.Task) return new FetchResult(FetchOutcome.Failed, Error: "Navigation timed out");
            var status = completed.Task.Result;

            var deadline = DateTime.UtcNow + ChallengeWait;
            while (true)
            {
                var title = await ReadAsync("document.title");
                var body = await ReadAsync("document.body ? document.body.innerText : ''");
                var trimmed = body.TrimStart();
                var isChallenge = title.Contains("Just a moment", StringComparison.OrdinalIgnoreCase)
                    || trimmed.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase);

                if (!isChallenge && (trimmed.StartsWith('{') || trimmed.StartsWith('[')))
                {
                    if (status is 401 or 403 || IsAuthError(trimmed)) return new FetchResult(FetchOutcome.SignedOut, trimmed, status);
                    return status is >= 200 and < 300 or 0
                        ? new FetchResult(FetchOutcome.Ok, trimmed, status)
                        : new FetchResult(FetchOutcome.Failed, trimmed, status, $"HTTP {status}");
                }

                if (DateTime.UtcNow > deadline)
                {
                    return isChallenge
                        ? new FetchResult(FetchOutcome.Challenge, Status: status, Error: "Cloudflare check did not clear")
                        : new FetchResult(FetchOutcome.Failed, Status: status, Error: $"Unexpected response (HTTP {status})");
                }

                await Task.Delay(1000);
            }
        }
        finally
        {
            core.NavigationCompleted -= OnCompleted;
        }
    }

    private static bool IsAuthError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("type", out var type)
                && type.GetString() is "authentication_error" or "permission_error";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<string> ReadAsync(string script)
    {
        var raw = await view.CoreWebView2.ExecuteScriptAsync(script);
        return JsonSerializer.Deserialize<string>(raw) ?? "";
    }

    public void Dispose()
    {
        view.Dispose();
        host.Dispose();
    }
}
