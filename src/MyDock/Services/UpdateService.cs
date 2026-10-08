using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace MyDock.Services;

/// <summary>GitHub 최신 릴리스 중 지금보다 새 버전. SetupUrl 이 null 이면 setup 자산이 없음(릴리스 페이지만).</summary>
public sealed record UpdateInfo(
    Version Version,
    string Tag,
    string Notes,
    string HtmlUrl,
    string? SetupName,
    string? SetupUrl,
    long SetupSize,
    string? SetupSha256)
{
    public string VersionText => Version.ToString(3);
}

public enum UpdateCheckResult
{
    /// <summary>지금 버전이 최신.</summary>
    UpToDate,
    /// <summary>새 버전 있음 (<see cref="UpdateService.Pending"/>).</summary>
    Available,
    /// <summary>새 버전이 있지만 "이 버전 건너뛰기" 한 버전 (자동 확인에서만).</summary>
    Skipped,
    /// <summary>네트워크·API 오류 (<see cref="UpdateService.LastError"/>).</summary>
    Failed,
}

/// <summary>
/// GitHub 릴리스(mong-head/mongdock)로 새 버전 확인 + setup.exe 다운로드·실행.
/// - 자동 확인: 시작 1분 뒤 + 12시간마다. Settings.CheckForUpdates 가 꺼져 있거나 일시 정지 중이면 건너뜀.
/// - /releases/latest 는 드래프트·프리릴리스를 돌려주지 않지만 응답 플래그도 한 번 더 확인.
/// - ETag/If-None-Match: 304 면 레이트리밋에 안 잡히고 이전 응답을 다시 씀.
/// - 이벤트는 모두 UI 스레드 (만든 스레드의 Dispatcher).
/// </summary>
public sealed class UpdateService : IDisposable
{
    public const string Repo = "mong-head/mongdock";
    public const string ReleasesPageUrl = "https://github.com/" + Repo + "/releases";
    private const string LatestApiUrl = "https://api.github.com/repos/" + Repo + "/releases/latest";
    private const string DownloadPrefix = "https://github.com/" + Repo + "/releases/download/";
    /// <summary>installer/mongdock.iss 의 AppId (Inno 는 HKCU\...\Uninstall\{AppId}_is1 에 설치 정보를 남김).</summary>
    private const string InnoUninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{E7300DFF-4D79-4C67-BF72-F83A83F78D73}_is1";

    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(12);
    private static readonly Regex SetupNamePattern = new(@"^mongdock-v\d+\.\d+(\.\d+)?[-\w.]*-setup\.exe$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>App 이 만든 인스턴스 (UI 가 메뉴·설정 창에서 씀). 없으면 null.</summary>
    public static UpdateService? Instance { get; private set; }

    private readonly SettingsService _settings;
    private readonly Func<bool> _isPaused;
    private readonly Dispatcher _dispatcher;
    private readonly HttpClient _api;
    private readonly HttpClient _download;
    private readonly DispatcherTimer _timer;
    private string? _etag;
    private UpdateInfo? _cachedLatest; // ETag 응답에 대응하는 (현재보다 새 버전일 때만) 릴리스
    private bool _cachedHasRelease;
    private CancellationTokenSource? _downloadCts;
    private bool _disposed;

    public UpdateService(SettingsService settings, Func<bool> isPaused)
    {
        _settings = settings;
        _isPaused = isPaused;
        _dispatcher = Dispatcher.CurrentDispatcher;

        string ua = $"{AppInfo.Name}/{WhatsNew.CurrentText} (+https://github.com/{Repo})";
        _api = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _api.DefaultRequestHeaders.UserAgent.ParseAdd(ua);
        _api.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _api.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        // 다운로드는 큰 파일이라 전체 시간 제한 대신 읽기마다 취소 토큰(사용자 취소) + 연결 시간 제한
        _download = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15) }) { Timeout = Timeout.InfiniteTimeSpan };
        _download.DefaultRequestHeaders.UserAgent.ParseAdd(ua);

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = FirstDelay };
        _timer.Tick += OnTimer;
        _settings.SettingsChanged += OnSettingsChanged;
        Instance = this;
    }

    // ───────────────────────── 상태 (UI 가 읽음) ─────────────────────────

    /// <summary>알릴 새 버전 (건너뛴 버전 제외). 없으면 null.</summary>
    public UpdateInfo? Pending { get; private set; }

    public bool IsChecking { get; private set; }
    public DateTime? LastChecked { get; private set; }
    public UpdateCheckResult? LastResult { get; private set; }
    public string? LastError { get; private set; }

    public bool IsDownloading { get; private set; }
    /// <summary>0~1 다운로드 진행률.</summary>
    public double DownloadProgress { get; private set; }
    public long DownloadedBytes { get; private set; }
    /// <summary>마지막 다운로드/실행 실패 이유 (사용자에게 보여 줄 한 줄). 성공·취소면 null.</summary>
    public string? DownloadError { get; private set; }

    /// <summary>Pending·확인 중·다운로드 시작/끝 등 상태가 바뀜 (UI 스레드).</summary>
    public event EventHandler? Changed;
    /// <summary>다운로드 진행률 (UI 스레드, 자주 옴).</summary>
    public event EventHandler? ProgressChanged;
    /// <summary>새 버전을 (Pending 으로) 발견함 — 배너 등. 같은 버전이면 다시 안 옴 (UI 스레드).</summary>
    public event EventHandler<UpdateInfo>? Available;

    // ───────────────────────── 주기 확인 ─────────────────────────

    /// <summary>자동 확인 시작 (1분 뒤 첫 확인).</summary>
    public void Start()
    {
        if (_disposed) return;
        _timer.Interval = FirstDelay;
        _timer.Start();
    }

    private void OnTimer(object? sender, EventArgs e)
    {
        _timer.Interval = Interval;
        if (!_settings.Current.CheckForUpdates || _isPaused()) return;
        _ = CheckAsync(manual: false);
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        // 건너뛰기 해제·설정 파일 직접 편집 등 → Pending 다시 계산
        RecomputePending(raise: true);
    }

    /// <summary>
    /// 지금 확인. manual(설정 창 "업데이트 확인") 이면 건너뛴 버전도 다시 알림(건너뛰기 해제).
    /// 이미 확인 중이면 그 결과를 기다리지 않고 현재 결과를 돌려줌.
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(bool manual)
    {
        if (_disposed) return UpdateCheckResult.Failed;
        if (IsChecking) return LastResult ?? UpdateCheckResult.UpToDate;
        IsChecking = true;
        RaiseChanged();
        UpdateCheckResult result;
        try
        {
            var latest = await FetchLatestAsync().ConfigureAwait(true);
            LastError = null;
            LastChecked = DateTime.Now;
            _cachedLatest = latest;
            if (latest is null)
            {
                result = UpdateCheckResult.UpToDate;
            }
            else
            {
                if (manual && IsSkipped(latest))
                {
                    _settings.Current.SkippedUpdateVersion = null;
                    _settings.Save();
                }
                result = IsSkipped(latest) ? UpdateCheckResult.Skipped : UpdateCheckResult.Available;
            }
            Log.Info($"업데이트 확인{(manual ? " (수동)" : "")}: 현재 {WhatsNew.CurrentText}, " +
                     (latest is null ? "최신 버전" : $"새 버전 {latest.Tag}{(result == UpdateCheckResult.Skipped ? " (건너뛴 버전)" : "")}"));
        }
        catch (Exception ex)
        {
            result = UpdateCheckResult.Failed;
            LastError = ex switch
            {
                TaskCanceledException => "응답이 없어요 (15초)",
                HttpRequestException h when h.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests => "GitHub 요청 한도를 넘었어요. 잠시 뒤 다시 시도해 주세요.",
                HttpRequestException => "인터넷에 연결할 수 없어요",
                _ => "확인하지 못했어요",
            };
            Log.Warn($"업데이트 확인 실패 ({LatestApiUrl}): {ex.Message}");
        }
        finally
        {
            IsChecking = false;
        }
        LastResult = result;
        RecomputePending(raise: false);
        RaiseChanged();
        return result;
    }

    /// <summary>GitHub 최신 릴리스 → 지금보다 새 버전이면 UpdateInfo, 아니면 null. 304 면 이전 응답.</summary>
    private async Task<UpdateInfo?> FetchLatestAsync()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, LatestApiUrl);
        if (_etag is not null && _cachedHasRelease)
            req.Headers.TryAddWithoutValidation("If-None-Match", _etag);
        using var resp = await _api.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotModified)
            return _cachedLatest;
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            // 릴리스가 하나도 없거나 (전부 프리릴리스·드래프트)
            _etag = null;
            _cachedHasRelease = false;
            return null;
        }
        resp.EnsureSuccessStatusCode();
        string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        var info = Parse(json);
        _etag = resp.Headers.ETag?.ToString();
        _cachedHasRelease = true;
        return info;
    }

    /// <summary>릴리스 JSON → 지금보다 새 정식 버전이면 UpdateInfo.</summary>
    private static UpdateInfo? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (Bool(r, "draft") || Bool(r, "prerelease")) return null;
        string tag = Str(r, "tag_name") ?? "";
        var version = WhatsNew.Parse(tag);
        if (version is null || version <= WhatsNew.Current) return null;

        string? setupName = null, setupUrl = null, sha = null;
        long size = 0;
        if (r.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                string? name = Str(a, "name");
                string? url = Str(a, "browser_download_url");
                if (name is null || url is null || !SetupNamePattern.IsMatch(name)) continue;
                if (Str(a, "state") is { } state && state != "uploaded") continue;
                setupName = name;
                setupUrl = url;
                size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out long n) ? n : 0;
                if (Str(a, "digest") is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    sha = digest["sha256:".Length..].Trim().ToLowerInvariant();
                break;
            }
        }
        return new UpdateInfo(version, tag, Str(r, "body") ?? "", Str(r, "html_url") ?? ReleasesPageUrl,
            setupName, setupUrl, size, sha);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private bool IsSkipped(UpdateInfo info) =>
        WhatsNew.Parse(_settings.Current.SkippedUpdateVersion) is { } skipped && skipped == info.Version;

    /// <summary>캐시된 최신 릴리스와 건너뛰기 설정으로 Pending 다시 계산. 새 버전이 Pending 이 되면 Available.</summary>
    private void RecomputePending(bool raise)
    {
        var latest = _cachedLatest;
        var next = latest is not null && !IsSkipped(latest) ? latest : null;
        bool changed = !ReferenceEquals(next, Pending);
        bool isNew = next is not null && next.Version != Pending?.Version;
        Pending = next;
        if (changed && raise) RaiseChanged();
        if (isNew && next is not null)
        {
            try { Available?.Invoke(this, next); }
            catch (Exception ex) { Log.Error("업데이트 알림 처리 중 예외", ex); }
        }
    }

    /// <summary>"이 버전 건너뛰기": 저장 → Pending 사라짐.</summary>
    public void Skip(UpdateInfo info)
    {
        _settings.Current.SkippedUpdateVersion = info.VersionText;
        _settings.Save(); // SettingsChanged → RecomputePending
        RecomputePending(raise: true);
        Log.Info($"업데이트 건너뜀: {info.Tag}");
    }

    /// <summary>배너를 이 버전에 대해 처음 띄우는지 (true 면 기록까지 함).</summary>
    public bool MarkNotified(UpdateInfo info)
    {
        if (WhatsNew.Parse(_settings.Current.NotifiedUpdateVersion) == info.Version) return false;
        _settings.Current.NotifiedUpdateVersion = info.VersionText;
        _settings.Save();
        return true;
    }

    // ───────────────────────── 설치 방식 ─────────────────────────

    /// <summary>installer/mongdock.iss 의 DefaultDirName ({localappdata}\Programs\mongdock, 폴더 선택 화면 없음).</summary>
    private static readonly string InnoDefaultDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppInfo.Name);

    /// <summary>
    /// setup.exe 로 덮어써도 되는 설치인지: Inno 설치 프로그램이 남긴 언인스톨 정보가 있고 그 설치 위치가 지금 실행 중인 폴더.
    /// 아니면(zip 으로 다른 곳에 풀었거나 개발 빌드) reason 에 한 줄 이유.
    /// </summary>
    public static bool CanSelfUpdate(out string reason)
    {
        reason = "";
        try
        {
            string running = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
            // zip 을 설치 프로그램 기본 위치(%LOCALAPPDATA%\Programs\mongdock)에 풀었으면 setup.exe 도 같은 곳에 덮어쓰므로 허용
            if (string.Equals(running, InnoDefaultDirectory, StringComparison.OrdinalIgnoreCase)) return true;

            using var key = Registry.CurrentUser.OpenSubKey(InnoUninstallKey);
            string? location = key?.GetValue("InstallLocation") as string;
            if (string.IsNullOrWhiteSpace(location))
            {
                reason = "설치 프로그램으로 설치하지 않아서(zip) 자동 업데이트를 할 수 없어요. 릴리스 페이지에서 받아 주세요.";
                return false;
            }
            string installed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(location));
            if (!string.Equals(installed, running, StringComparison.OrdinalIgnoreCase))
            {
                reason = "설치 프로그램이 설치한 폴더가 아닌 곳에서 실행 중이라(zip) 자동 업데이트를 할 수 없어요. 릴리스 페이지에서 받아 주세요.";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"설치 방식 확인 실패: {ex.Message}");
            reason = "설치 정보를 읽지 못해 자동 업데이트를 할 수 없어요. 릴리스 페이지에서 받아 주세요.";
            return false;
        }
    }

    // ───────────────────────── 다운로드 + 설치 ─────────────────────────

    /// <summary>업데이트 파일 폴더 (%LOCALAPPDATA%\mongdock\updates).</summary>
    public static string UpdatesDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.Name, "updates");

    /// <summary>
    /// "지금 업데이트": setup 자산 다운로드 → 크기·SHA-256(있으면)·파일명 확인 → 설정 저장 → setup.exe /SILENT 실행.
    /// 설치 프로그램이 몽독을 정상 종료(Local\mongdock.Exit)하고 덮어쓴 뒤 다시 실행한다. 성공하면 true (곧 종료됨).
    /// </summary>
    public async Task<bool> DownloadAndInstallAsync(UpdateInfo info)
    {
        if (_disposed || IsDownloading) return false;
        DownloadError = null;
        if (!CanSelfUpdate(out string why))
        {
            DownloadError = why;
            RaiseChanged();
            return false;
        }
        if (info.SetupUrl is null || info.SetupName is null || info.SetupSize <= 0 ||
            !SetupNamePattern.IsMatch(info.SetupName) ||
            !info.SetupUrl.StartsWith(DownloadPrefix, StringComparison.OrdinalIgnoreCase) ||
            !info.SetupUrl.EndsWith("/" + info.SetupName, StringComparison.OrdinalIgnoreCase))
        {
            DownloadError = "이 릴리스에는 설치 파일이 없어요. 릴리스 페이지에서 받아 주세요.";
            Log.Warn($"업데이트 설치 파일 없음/형식 다름: {info.Tag} {info.SetupUrl}");
            RaiseChanged();
            return false;
        }

        var cts = new CancellationTokenSource();
        _downloadCts = cts;
        IsDownloading = true;
        DownloadProgress = 0;
        DownloadedBytes = 0;
        RaiseChanged();

        string? path = null;
        try
        {
            path = await DownloadAsync(info, cts.Token).ConfigureAwait(true);
            Log.Info($"업데이트 다운로드 완료: {info.SetupUrl} → {path} ({info.SetupSize:N0} bytes{(info.SetupSha256 is null ? "" : ", SHA-256 일치")})");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Log.Info($"업데이트 다운로드 취소: {info.SetupUrl}");
        }
        catch (Exception ex)
        {
            DownloadError = ex is InvalidDataException ? ex.Message : "다운로드하지 못했어요. 인터넷 연결을 확인해 주세요.";
            Log.Warn($"업데이트 다운로드 실패: {info.SetupUrl}: {ex.Message}");
        }
        finally
        {
            IsDownloading = false;
            _downloadCts = null;
            cts.Dispose();
        }

        if (path is null || _disposed)
        {
            RaiseChanged();
            return false;
        }

        try
        {
            // 설치 프로그램이 곧 몽독을 끄므로 지금 설정을 저장해 둠
            try { _settings.Save(); }
            catch (Exception ex) { Log.Warn($"업데이트 전 설정 저장 실패: {ex.Message}"); }
            // 조용한 설치는 작업 선택 화면이 없어 "로그인 시 자동 실행"(기본 체크)이 새로 켜질 수 있음 →
            // 지금 자동 실행이 꺼져 있으면 그 작업을 빼서 그대로 둔다 (켜져 있으면 설치 프로그램이 유지).
            string args = "/SILENT /SUPPRESSMSGBOXES /NORESTART";
            if (!AutoStartRegistered()) args += " /MERGETASKS=\"!autostart\"";
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                Arguments = args,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(path)!,
            })?.Dispose();
            Log.Info($"업데이트 설치 시작: {Path.GetFileName(path)} {args}");
            RaiseChanged();
            return true;
        }
        catch (Exception ex)
        {
            DownloadError = "설치 프로그램을 실행하지 못했어요.";
            Log.Error($"업데이트 설치 프로그램 실행 실패: {path}", ex);
            RaiseChanged();
            return false;
        }
    }

    /// <summary>HKCU Run 에 "mongdock" 값이 있는지 (StartupService·설치 프로그램과 같은 값).</summary>
    private static bool AutoStartRegistered()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            return run?.GetValue(AppInfo.Name) is string;
        }
        catch { return true; } // 모르면 설치 프로그램 판단(이전 선택 기억)에 맡김
    }

    /// <summary>진행 중인 다운로드 취소.</summary>
    public void CancelDownload()
    {
        try { _downloadCts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private async Task<string> DownloadAsync(UpdateInfo info, CancellationToken ct)
    {
        Directory.CreateDirectory(UpdatesDirectory);
        CleanOldFiles(keep: info.SetupName!);
        string final = Path.Combine(UpdatesDirectory, info.SetupName!);
        string partial = final + ".partial";

        // 이미 받아 둔 같은 파일이 있으면 검증만 하고 재사용
        if (File.Exists(final))
        {
            try
            {
                await VerifyAsync(final, info, ct).ConfigureAwait(false);
                Report(info.SetupSize, info.SetupSize);
                return final;
            }
            catch (InvalidDataException) { File.Delete(final); }
        }

        using (var resp = await _download.GetAsync(info.SetupUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            if (resp.Content.Headers.ContentLength is long len && len != info.SetupSize)
                throw new InvalidDataException("받은 파일 크기가 릴리스 정보와 달라요.");
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buffer = new byte[81920];
            long total = 0;
            long lastReport = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > info.SetupSize) throw new InvalidDataException("받은 파일이 릴리스 정보보다 커요.");
                await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                if (total - lastReport >= 256 * 1024 || total == info.SetupSize)
                {
                    lastReport = total;
                    Report(total, info.SetupSize);
                }
            }
        }

        try
        {
            await VerifyAsync(partial, info, ct).ConfigureAwait(false);
            File.Move(partial, final, overwrite: true);
            return final;
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    /// <summary>크기(GitHub size) · 실행 파일 머리(MZ) · SHA-256(GitHub digest 가 있으면) 확인.</summary>
    private static async Task VerifyAsync(string path, UpdateInfo info, CancellationToken ct)
    {
        var fi = new FileInfo(path);
        if (fi.Length != info.SetupSize)
            throw new InvalidDataException($"받은 파일 크기({fi.Length:N0})가 릴리스 정보({info.SetupSize:N0})와 달라요.");
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var head = new byte[2];
        if (await fs.ReadAsync(head, ct).ConfigureAwait(false) != 2 || head[0] != (byte)'M' || head[1] != (byte)'Z')
            throw new InvalidDataException("받은 파일이 실행 파일이 아니에요.");
        if (info.SetupSha256 is null) return;
        fs.Position = 0;
        byte[] hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        if (!string.Equals(Convert.ToHexString(hash), info.SetupSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("받은 파일의 SHA-256 이 릴리스 정보와 달라요.");
    }

    private void Report(long done, long total)
    {
        _dispatcher.BeginInvoke(() =>
        {
            DownloadedBytes = done;
            DownloadProgress = total > 0 ? Math.Clamp((double)done / total, 0, 1) : 0;
            try { ProgressChanged?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { Log.Error("업데이트 진행률 처리 중 예외", ex); }
        });
    }

    /// <summary>예전 업데이트 파일 정리 (지금 받을 파일만 남김).</summary>
    private static void CleanOldFiles(string keep)
    {
        try
        {
            foreach (var f in Directory.GetFiles(UpdatesDirectory))
                if (!string.Equals(Path.GetFileName(f), keep, StringComparison.OrdinalIgnoreCase))
                    TryDelete(f);
        }
        catch { }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { }
    }

    private void RaiseChanged()
    {
        if (_disposed) return;
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(RaiseChanged);
            return;
        }
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { Log.Error("업데이트 상태 처리 중 예외", ex); }
    }

    // ───────────────────────── 릴리스 노트 ─────────────────────────

    /// <summary>마크다운 릴리스 노트 → 간단한 텍스트 (제목 기호·강조·코드·링크 주소 제거, 목록은 •).</summary>
    public static string NotesToText(string markdown, int maxChars = 4000)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        var sb = new StringBuilder();
        bool blank = false;
        foreach (string raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) continue;
            line = Regex.Replace(line, @"^\s{0,3}#{1,6}\s*", "");
            line = Regex.Replace(line, @"^(\s*)[-*+]\s+", "$1• ");
            line = Regex.Replace(line, @"!\[([^\]]*)\]\([^)]*\)", "$1");
            line = Regex.Replace(line, @"\[([^\]]+)\]\([^)]*\)", "$1");
            line = line.Replace("**", "").Replace("__", "").Replace("`", "");
            line = Regex.Replace(line, @"<[^>]+>", "");
            if (line.Trim().Length == 0)
            {
                if (!blank && sb.Length > 0) sb.Append('\n');
                blank = true;
                continue;
            }
            blank = false;
            sb.Append(line).Append('\n');
            if (sb.Length > maxChars) { sb.Length = maxChars; sb.Append('…'); break; }
        }
        return sb.ToString().Trim();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _settings.SettingsChanged -= OnSettingsChanged;
        CancelDownload();
        _api.Dispose();
        _download.Dispose();
        if (Instance == this) Instance = null;
    }
}
