using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace Mongdock.Services;

/// <summary>
/// 앱 전용 메뉴 규칙(menus/app-menus.json)을 GitHub 에서 받아 갱신.
/// - 시작: 캐시(%APPDATA%\mongdock\cache\app-menus.json)와 내장 리소스 중 revision 이 높은 것(같으면 캐시). schema 가 다르면 그 파일은 무시.
/// - 조회: 시작 2분 뒤, 그 뒤 24시간마다. If-None-Match(ETag), 15초 시간 초과, 512KB 상한. 일시 정지 중이면 1시간 뒤 다시 시도,
///   settings.updateMenuRules=false 면 받지 않음.
/// - 받은 파일은 <see cref="MenuRules.Parse"/> 로 검증(잘못된 앱 항목만 버림)한 뒤 원자적으로 캐시에 저장하고 즉시 반영(<see cref="RulesChanged"/>).
/// - 실패(404·네트워크·검증 실패)는 로그 한 줄만 남기고 지금 규칙을 그대로 쓴다.
/// </summary>
internal sealed class MenuRulesService : IDisposable
{
    public const string RemoteUrl = "https://raw.githubusercontent.com/mong-head/mongdock/menus-stable/menus/app-menus.json";

    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private static readonly TimeSpan PausedRetry = TimeSpan.FromHours(1);

    private static readonly string CacheDir = Path.Combine(AppInfo.DataDirectory, "cache");
    private static readonly string CachePath = Path.Combine(CacheDir, "app-menus.json");
    private static readonly string EtagPath = Path.Combine(CacheDir, "app-menus.etag");

    private readonly ISettingsService _settings;
    private readonly MenuRuleSet _embedded;
    private readonly CancellationTokenSource _cts = new();
    private readonly Timer _timer;
    private volatile MenuRuleSet _current;
    private int _running;
    private HttpClient? _http;

    /// <summary>규칙이 바뀌어 지금 쓰는 묶음(<see cref="Current"/>)이 교체됨. 백그라운드 스레드.</summary>
    public event Action? RulesChanged;

    public MenuRulesService(ISettingsService settings)
    {
        _settings = settings;
        _embedded = MenuRules.LoadEmbedded();
        _current = Choose(_embedded, LoadCache());
        Log.Info($"앱 메뉴 규칙: {_current.Source} revision {_current.Revision} (앱 {_current.Apps.Count}개, 내장 revision {_embedded.Revision})");
        _timer = new Timer(_ => _ = TickAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>지금 쓰는 규칙 (스레드 안전, 통째로 교체됨).</summary>
    public MenuRuleSet Current => _current;

    /// <summary>2분 뒤 첫 조회를 예약.</summary>
    public void Start() => Schedule(FirstDelay);

    public void Dispose()
    {
        _cts.Cancel();
        _timer.Dispose();
        _http?.Dispose();
    }

    private void Schedule(TimeSpan due)
    {
        if (_cts.IsCancellationRequested) return;
        try { _timer.Change(due, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    private async Task TickAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        TimeSpan next = Interval;
        try
        {
            if (ViewModels.AppState.Paused) next = PausedRetry;
            else if (_settings.Current.UpdateMenuRules) await CheckAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (_cts.IsCancellationRequested && ex is OperationCanceledException or ObjectDisposedException or HttpRequestException)
        {
            return; // 종료 중 (Dispose 가 HttpClient 를 닫음) — 로그 없이
        }
        catch (Exception ex)
        {
            Log.Error("앱 메뉴 규칙 갱신 중 오류", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
        Schedule(next);
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        _http ??= CreateHttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, RemoteUrl);
        string? etag = File.Exists(CachePath) ? ReadEtag() : null; // 캐시가 없으면 304 를 받아도 쓸 게 없음
        if (etag is not null && EntityTagHeaderValue.TryParse(etag, out var tag)) req.Headers.IfNoneMatch.Add(tag);

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException)
        {
            Log.Info($"앱 메뉴 규칙 확인 실패({(ex is TaskCanceledException ? "시간 초과" : ex.Message)}) → 지금 규칙 유지 ({_current.Source} revision {_current.Revision})");
            return;
        }

        using (resp)
        {
            if (resp.StatusCode == HttpStatusCode.NotModified)
            {
                Log.Info($"앱 메뉴 규칙 변경 없음 (revision {_current.Revision})");
                return;
            }
            if (!resp.IsSuccessStatusCode)
            {
                Log.Info($"앱 메뉴 규칙 원격 파일 없음/오류 (HTTP {(int)resp.StatusCode}) → 지금 규칙 유지 ({_current.Source} revision {_current.Revision})");
                return;
            }
            if (resp.Content.Headers.ContentLength is long len && len > MenuRules.MaxBytes)
            {
                Log.Warn($"앱 메뉴 규칙 원격 파일이 너무 큼 ({len} bytes) → 무시");
                return;
            }

            string? text = await ReadLimitedAsync(resp.Content, ct).ConfigureAwait(false);
            if (text is null)
            {
                Log.Warn($"앱 메뉴 규칙 원격 파일이 {MenuRules.MaxBytes / 1024}KB 를 넘음 → 무시");
                return;
            }

            var set = MenuRules.Parse(text, "원격", out string? error);
            if (set is null)
            {
                Log.Warn($"앱 메뉴 규칙 원격 파일 무시: {error}");
                return;
            }

            try
            {
                AtomicFile.WriteAllText(CachePath, text);
                string? newTag = resp.Headers.ETag?.ToString();
                if (newTag is not null) AtomicFile.WriteAllText(EtagPath, newTag);
                else if (File.Exists(EtagPath)) File.Delete(EtagPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"앱 메뉴 규칙 캐시 저장 실패: {ex.Message}");
            }

            Apply(Choose(_embedded, set), $"원격 revision {set.Revision} 받음");
        }
    }

    private void Apply(MenuRuleSet next, string why)
    {
        var prev = _current;
        if (ReferenceEquals(prev, next) || (prev.Revision == next.Revision && prev.RawText == next.RawText))
        {
            Log.Info($"앱 메뉴 규칙: {why} — 지금 규칙과 같음 (revision {prev.Revision})");
            return;
        }
        _current = next;
        Log.Info($"앱 메뉴 규칙 갱신: {why} → {next.Source} revision {next.Revision} 사용 (이전 {prev.Source} revision {prev.Revision}, 앱 {next.Apps.Count}개)");
        try { RulesChanged?.Invoke(); }
        catch (Exception ex) { Log.Error("앱 메뉴 규칙 변경 알림 실패", ex); }
    }

    /// <summary>내장과 캐시/원격 중 revision 이 높은 것 (같으면 캐시/원격). 둘 다 schema 는 이미 검증됨.</summary>
    private static MenuRuleSet Choose(MenuRuleSet embedded, MenuRuleSet? downloaded) =>
        downloaded is not null && downloaded.Revision >= embedded.Revision ? downloaded : embedded;

    private static MenuRuleSet? LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            var fi = new FileInfo(CachePath);
            if (fi.Length > MenuRules.MaxBytes)
            {
                Log.Warn("앱 메뉴 규칙 캐시가 너무 큼 → 무시");
                return null;
            }
            var set = MenuRules.Parse(File.ReadAllText(CachePath, Encoding.UTF8), "캐시", out string? error);
            if (set is null) Log.Warn($"앱 메뉴 규칙 캐시 무시: {error}");
            return set;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"앱 메뉴 규칙 캐시 읽기 실패: {ex.Message}");
            return null;
        }
    }

    private static string? ReadEtag()
    {
        try
        {
            if (!File.Exists(EtagPath)) return null;
            string s = File.ReadAllText(EtagPath).Trim();
            return s.Length is > 0 and < 200 ? s : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>본문을 UTF-8 로 읽되 상한을 넘으면 null.</summary>
    private static async Task<string?> ReadLimitedAsync(HttpContent content, CancellationToken ct)
    {
        await using var s = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buf = new byte[16 * 1024];
        int n;
        while ((n = await s.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > MenuRules.MaxBytes) return null;
        }
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetString(ms.GetBuffer(), 0, (int)ms.Length).TrimStart('﻿');
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppInfo.Name, "1.0"));
        return client;
    }
}
