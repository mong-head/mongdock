using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace Mongdock.Services;

/// <summary>
/// 신고·사용 통계를 받는 주소 (Apps Script 웹 앱 …/exec). Code.gs 를 "새 배포"로 올리면 주소가 바뀌어 앱을 고쳐야 했음 →
/// 홈페이지의 endpoint.json {"intake": "…/exec"} 을 읽어 씀 (하루 1회, cache/endpoint.json 에 기억). 못 읽으면 기억한 주소, 그것도 없으면 내장 주소.
/// 보낼 때만 읽음 (보낼 게 없으면 요청하지 않음). 받은 주소는 script.google.com/macros/s/…/exec 꼴일 때만 씀 (홈페이지가 잘못돼도 엉뚱한 곳으로 보내지 않게).
/// </summary>
internal static class IntakeEndpoint
{
    /// <summary>내장 주소 (2026-10-11 배포).</summary>
    public const string Builtin = "https://script.google.com/macros/s/AKfycbw1Xy5ktS0JtSQdvYRCvx9OymyO3TPNoJoUkgHXxwzvGslVy5COXDt1RGDfkWKCMugH/exec";
    public const string RemoteUrl = "https://mong-head.github.io/mongdock-site/endpoint.json";

    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);
    private static readonly string CachePath = Path.Combine(AppInfo.DataDirectory, "cache", "endpoint.json");
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? _cached;
    private static DateTime _cachedAt;
    private static bool _loaded;

    /// <summary>지금 쓸 주소 (기다리지 않음).</summary>
    public static string Current
    {
        get
        {
            Load();
            return _cached ?? Builtin;
        }
    }

    /// <summary>보내기 전에: 하루가 지났으면 홈페이지에서 새로 읽고 쓸 주소를 돌려줌. 실패해도 늘 쓸 주소가 있음.</summary>
    public static async Task<string> GetAsync(CancellationToken ct = default)
    {
        Load();
        if (DateTime.UtcNow - _cachedAt < MaxAge) return Current;
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (DateTime.UtcNow - _cachedAt < MaxAge) return Current;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(8));
                string text = await ReportService.Http.GetStringAsync(RemoteUrl, cts.Token).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(text);
                string? url = doc.RootElement.TryGetProperty("intake", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() : null;
                if (url is not null && IsValid(url))
                {
                    if (url != _cached) Log.Info("받는 주소: 홈페이지 주소로 바꿈");
                    Save(url);
                }
                else
                {
                    Log.Warn("받는 주소: 홈페이지 endpoint.json 이 올바르지 않음 — 이전 주소 그대로");
                    Save(_cached); // 하루 동안 다시 묻지 않음
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException)
            {
                // 홈페이지에 닿지 않음 — 이번엔 기억한 주소(또는 내장)로, 다음 보낼 때 다시
                Log.Info($"받는 주소: 홈페이지를 못 읽음 ({ex.GetType().Name}) — {(_cached is null ? "내장" : "기억한")} 주소로");
            }
            return Current;
        }
        finally { Gate.Release(); }
    }

    internal static bool IsValid(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
        && u.Host.Equals("script.google.com", StringComparison.OrdinalIgnoreCase)
        && u.AbsolutePath.StartsWith("/macros/s/", StringComparison.Ordinal) && u.AbsolutePath.EndsWith("/exec", StringComparison.Ordinal)
        && string.IsNullOrEmpty(u.Query);

    private static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(CachePath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(CachePath));
            var r = doc.RootElement;
            if (r.TryGetProperty("intake", out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { } url && IsValid(url)) _cached = url;
            if (r.TryGetProperty("at", out var at) && at.TryGetDateTime(out var t)) _cachedAt = t.ToUniversalTime();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    private static void Save(string? url)
    {
        _cached = url;
        _cachedAt = DateTime.UtcNow;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            AtomicFile.WriteAllText(CachePath, JsonSerializer.Serialize(new { intake = url, at = _cachedAt }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
