using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Mongdock.Models;
using Mongdock.Services;
using Mongdock.Services.Search;

namespace Mongdock.ViewModels;

/// <summary>Spotlight 결과 카테고리. 숫자 순서 = 화면에 보이는 순서 (최상위 히트 우선순위도 이 순서: 계산기 &gt; 앱 &gt; 설정 &gt; 파일).</summary>
public enum SpotlightCategory { TopHit, Calculator, Recent, Apps, Settings, Folders, Documents, Media, OtherFiles, Fallback }

/// <summary>Spotlight 결과 한 줄.</summary>
public sealed class SpotlightItem
{
    public required SpotlightCategory Category { get; init; }
    /// <summary>선택 유지용 식별자 (같은 대상이면 같은 값).</summary>
    public required string Key { get; init; }
    public required string Title { get; init; }
    /// <summary>제목 아래 회색 한 줄 (파일이면 들어 있는 폴더 경로).</summary>
    public string? Subtitle { get; init; }
    /// <summary>부제가 경로면 넘칠 때 앞부분을 말줄임 (끝의 폴더 이름이 보이게).</summary>
    public bool SubtitleIsPath { get; init; }
    /// <summary>아이콘 글리프 (Segoe Fluent Icons). 없으면 <see cref="LoadIcon"/>.</summary>
    public string? Glyph { get; init; }
    /// <summary>아이콘 캐시 키 + UI 스레드에서 부르는 아이콘 로더 (목록을 먼저 그리고 나중에 하나씩).</summary>
    public string? IconKey { get; init; }
    public Func<ImageSource?>? LoadIcon { get; init; }
    /// <summary>Enter / 클릭.</summary>
    public required Action Execute { get; init; }
    /// <summary>보조 동작 "폴더에서 보기" (Ctrl/Alt+Enter, 호버 버튼). 없으면 null.</summary>
    public Action? Reveal { get; init; }
    /// <summary>계산기: 실행하면 이 글자를 클립보드로 복사하고 "복사됨" 표시 후 닫음.</summary>
    public string? CopyText { get; init; }
}

/// <summary>머리글 하나 아래의 결과 묶음. Header 가 null 이면 머리글 없이 구분선만 (웹/Windows 검색).</summary>
public sealed record SpotlightSection(SpotlightCategory Category, string? Header, IReadOnlyList<SpotlightItem> Items);

/// <summary>검색 공급자. 빠른 공급자(앱·설정·계산기)는 입력 즉시, 느린 공급자(파일)는 디바운스 후 백그라운드.</summary>
public interface ISpotlightProvider
{
    string Name { get; }
    bool IsSlow { get; }
    Task<IReadOnlyList<SpotlightItem>> SearchAsync(string query, SearchSettings settings, CancellationToken ct);
}

// ───────────────────────── 공급자: 응용 프로그램 ─────────────────────────

/// <summary>시작 메뉴의 모든 앱 (shell:AppsFolder). 검색어가 비면 최근 실행한 앱.</summary>
public sealed class AppSearchProvider : ISpotlightProvider
{
    private readonly AppServices _services;
    private readonly IconStyle _iconStyle;
    /// <summary>창을 다시 열 때 바로 검색되게 정적 (AppsFolder 도 60초 캐시).</summary>
    public static IReadOnlyList<SpotlightApp> Apps { get; set; } = Array.Empty<SpotlightApp>();

    public AppSearchProvider(AppServices services, IconStyle iconStyle)
    {
        _services = services;
        _iconStyle = iconStyle;
    }

    public string Name => "앱";
    public bool IsSlow => false;

    public Task<IReadOnlyList<SpotlightItem>> SearchAsync(string query, SearchSettings settings, CancellationToken ct)
    {
        if (!settings.Apps) return Task.FromResult<IReadOnlyList<SpotlightItem>>(Array.Empty<SpotlightItem>());
        bool empty = query.Trim().Length == 0;
        var found = SpotlightMatcher.Search(Apps, query, SpotlightRecents.Items, empty ? SpotlightMatcher.MaxResults : settings.MaxPerCategory);
        var category = empty ? SpotlightCategory.Recent : SpotlightCategory.Apps;
        IReadOnlyList<SpotlightItem> items = found.Select(app => new SpotlightItem
        {
            Category = category,
            Key = "app:" + app.ParsingName,
            Title = app.Name,
            IconKey = "app:" + app.ParsingName,
            LoadIcon = () => _services.Icons.GetIcon(new PinItem { Name = app.Name, Kind = PinKind.Aumid, Target = app.ParsingName }, _iconStyle),
            Execute = () =>
            {
                SpotlightRecents.Add(app.ParsingName);
                // shell:AppsFolder\<파싱 이름> 실행 (AppLauncher 의 AUMID 경로 — 데스크톱 앱 항목도 동작)
                _services.Launcher.Launch(new PinItem { Name = app.Name, Kind = PinKind.Aumid, Target = app.ParsingName });
            },
        }).ToList();
        return Task.FromResult(items);
    }
}

// ───────────────────────── 공급자: 윈도우 설정 ─────────────────────────

/// <summary>ms-settings: 페이지 (이름·별칭, 한글 초성 검색).</summary>
public sealed class SettingsSearchProvider : ISpotlightProvider
{
    public string Name => "설정";
    public bool IsSlow => false;

    public Task<IReadOnlyList<SpotlightItem>> SearchAsync(string query, SearchSettings settings, CancellationToken ct)
    {
        string q = query.Trim();
        if (!settings.Settings || q.Length == 0) return Task.FromResult<IReadOnlyList<SpotlightItem>>(Array.Empty<SpotlightItem>());

        var hits = new List<(SettingsPage Page, int Tier)>();
        foreach (var page in SettingsPages.All)
        {
            int tier = SpotlightMatcher.Match(page.Name, q);
            foreach (string alias in page.Aliases)
            {
                int t = SpotlightMatcher.Match(alias, q);
                // 별칭 일치는 이름 일치보다 한 단계 아래 (같은 검색어면 이름이 맞는 페이지가 위로)
                if (t >= 0) t = Math.Min(t + 1, 3);
                if (t >= 0 && (tier < 0 || t < tier)) tier = t;
            }
            if (tier >= 0) hits.Add((page, tier));
        }
        IReadOnlyList<SpotlightItem> items = hits
            .OrderBy(h => h.Tier)
            .ThenBy(h => h.Page.Name.Length)
            .Take(settings.MaxPerCategory)
            .Select(h => new SpotlightItem
            {
                Category = SpotlightCategory.Settings,
                Key = "settings:" + h.Page.Uri,
                Title = h.Page.Name,
                Subtitle = "시스템 설정",
                Glyph = h.Page.Glyph,
                Execute = () => ShellOpen(h.Page.Uri),
            })
            .ToList();
        return Task.FromResult(items);
    }

    internal static void ShellOpen(string target)
        => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
}

// ───────────────────────── 공급자: 계산기 ─────────────────────────

public sealed class CalculatorProvider : ISpotlightProvider
{
    public string Name => "계산기";
    public bool IsSlow => false;

    public Task<IReadOnlyList<SpotlightItem>> SearchAsync(string query, SearchSettings settings, CancellationToken ct)
    {
        if (!settings.Calculator || !Calculator.TryEvaluate(query, out double v))
            return Task.FromResult<IReadOnlyList<SpotlightItem>>(Array.Empty<SpotlightItem>());
        string plain = Calculator.FormatPlain(v);
        IReadOnlyList<SpotlightItem> items = new[]
        {
            new SpotlightItem
            {
                Category = SpotlightCategory.Calculator,
                Key = "calc",
                Title = "= " + Calculator.FormatDisplay(v),
                Subtitle = query.Trim() + "  ·  Enter 로 결과 복사",
                Glyph = "", // Calculator
                CopyText = plain,
                Execute = () => Clipboard.SetText(plain),
            },
        };
        return Task.FromResult(items);
    }
}

// ───────────────────────── 공급자: 파일·폴더 (윈도우 검색 색인) ─────────────────────────

public sealed class FileSearchProvider : ISpotlightProvider
{
    public string Name => "파일";
    public bool IsSlow => true;

    public async Task<IReadOnlyList<SpotlightItem>> SearchAsync(string query, SearchSettings settings, CancellationToken ct)
    {
        string q = query.Trim();
        var categories = new List<FileCategory>();
        if (settings.Folders) categories.Add(FileCategory.Folder);
        if (settings.Documents) categories.Add(FileCategory.Document);
        if (settings.Media) categories.Add(FileCategory.Media);
        if (settings.OtherFiles) categories.Add(FileCategory.Other);
        if (q.Length == 0 || categories.Count == 0) return Array.Empty<SpotlightItem>();
        // 수식만 입력했으면 파일 이름 검색은 의미 없음
        if (settings.Calculator && Calculator.LooksLikeExpression(q)) return Array.Empty<SpotlightItem>();

        var folders = (settings.FileSearchFolders ?? new List<string>()).ToList();
        var files = await WindowsIndexSearch.SearchAsync(q, folders, categories, settings.MaxPerCategory, ct);
        return files.Select(ToItem).ToList();
    }

    private static SpotlightItem ToItem(IndexedFile f)
    {
        bool folder = f.Category == FileCategory.Folder;
        string path = f.Path;
        return new SpotlightItem
        {
            Category = f.Category switch
            {
                FileCategory.Folder => SpotlightCategory.Folders,
                FileCategory.Document => SpotlightCategory.Documents,
                FileCategory.Media => SpotlightCategory.Media,
                _ => SpotlightCategory.OtherFiles,
            },
            Key = "file:" + path,
            Title = f.Name,
            Subtitle = Path.GetDirectoryName(f.DisplayPath) ?? f.DisplayPath,
            SubtitleIsPath = true,
            IconKey = null, // 캐시는 ShellFileIcons 가 확장자별로
            LoadIcon = () => ShellFileIcons.Get(path, folder),
            // 폴더 = 탐색기로 열기, 파일 = 기본 앱으로 열기
            Execute = folder
                ? () => Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"\"{path}\"", UseShellExecute = true })?.Dispose()
                : () => SettingsSearchProvider.ShellOpen(path),
            Reveal = () => Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{path}\"", UseShellExecute = true })?.Dispose(),
        };
    }
}

// ───────────────────────── 공급자: 웹 검색 / Windows 검색 (맨 아래 고정) ─────────────────────────

public sealed class FallbackProvider : ISpotlightProvider
{
    private readonly Action<string> _sendToWindowsSearch;

    public FallbackProvider(Action<string> sendToWindowsSearch) => _sendToWindowsSearch = sendToWindowsSearch;

    public string Name => "웹/Windows 검색";
    public bool IsSlow => false;

    public Task<IReadOnlyList<SpotlightItem>> SearchAsync(string query, SearchSettings settings, CancellationToken ct)
    {
        string q = query.Trim();
        var items = new List<SpotlightItem>();
        if (q.Length == 0) return Task.FromResult<IReadOnlyList<SpotlightItem>>(items);
        if (settings.WindowsSearch)
            items.Add(new SpotlightItem
            {
                Category = SpotlightCategory.Fallback,
                Key = "windows-search",
                Title = $"Windows 검색에서 ‘{q}’ 찾기",
                Glyph = "", // Search
                Execute = () => _sendToWindowsSearch(q),
            });
        if (settings.WebSearch)
        {
            var engine = settings.WebSearchEngine;
            items.Add(new SpotlightItem
            {
                Category = SpotlightCategory.Fallback,
                Key = "web",
                Title = $"{EngineName(engine)}에서 ‘{q}’ 검색",
                Glyph = "", // Globe
                Execute = () =>
                {
                    SettingsSearchProvider.ShellOpen(WebSearchUrl(engine, q));
                    Log.Info("Spotlight 웹 검색");
                },
            });
        }
        return Task.FromResult<IReadOnlyList<SpotlightItem>>(items);
    }

    public static string EngineName(WebSearchEngine engine) => engine switch
    {
        WebSearchEngine.Naver => "네이버",
        WebSearchEngine.Bing => "Bing",
        _ => "Google",
    };

    public static string WebSearchUrl(WebSearchEngine engine, string q) => engine switch
    {
        WebSearchEngine.Naver => "https://search.naver.com/search.naver?query=" + Uri.EscapeDataString(q),
        WebSearchEngine.Bing => "https://www.bing.com/search?q=" + Uri.EscapeDataString(q),
        _ => "https://www.google.com/search?q=" + Uri.EscapeDataString(q),
    };
}

// ───────────────────────── 검색 세션: 공급자 묶기 + 디바운스 + 카테고리 정리 ─────────────────────────

/// <summary>
/// 입력마다 <see cref="Search"/>: 빠른 공급자 결과를 바로 <see cref="Updated"/> 로 내보내고,
/// 느린 공급자(파일)는 120ms 디바운스 뒤 백그라운드에서 → 끝나면 합쳐서 한 번 더 내보냄.
/// 새 입력이 오면 이전 느린 검색은 취소(결과 무시). UI 스레드에서만 호출 (이벤트도 UI 스레드에서).
/// </summary>
public sealed class SpotlightSearchSession : IDisposable
{
    public const int DebounceMs = 120;

    private readonly IReadOnlyList<ISpotlightProvider> _providers;
    private readonly Func<SearchSettings> _settings;
    private CancellationTokenSource? _cts;

    /// <summary>(검색어, 정리된 묶음, 같은 검색어의 늦게 온 추가 결과인지).</summary>
    public event Action<string, IReadOnlyList<SpotlightSection>, bool>? Updated;

    public SpotlightSearchSession(IReadOnlyList<ISpotlightProvider> providers, Func<SearchSettings> settings)
    {
        _providers = providers;
        _settings = settings;
    }

    public void Search(string query)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        var cts = new CancellationTokenSource();
        _cts = cts;
        var settings = _settings();

        var fast = new List<SpotlightItem>();
        foreach (var p in _providers.Where(p => !p.IsSlow))
        {
            try
            {
                var task = p.SearchAsync(query, settings, cts.Token);
                // 빠른 공급자는 동기 완료 (Task.FromResult)
                if (task.IsCompletedSuccessfully) fast.AddRange(task.Result);
                else Log.Warn($"Spotlight 공급자 '{p.Name}' 가 동기 완료되지 않아 생략");
            }
            catch (Exception ex)
            {
                Log.Error($"Spotlight 공급자 '{p.Name}' 실패", ex);
            }
        }
        Updated?.Invoke(query, Arrange(query, fast), false);

        var slow = _providers.Where(p => p.IsSlow).ToList();
        if (slow.Count > 0 && query.Trim().Length > 0) _ = RunSlowAsync(query, settings, fast, slow, cts);
    }

    private async Task RunSlowAsync(string query, SearchSettings settings, List<SpotlightItem> fast, List<ISpotlightProvider> slow, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(DebounceMs, cts.Token);
            var all = new List<SpotlightItem>(fast);
            bool any = false;
            foreach (var p in slow)
            {
                try
                {
                    var items = await p.SearchAsync(query, settings, cts.Token);
                    if (items.Count > 0) { all.AddRange(items); any = true; }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log.Error($"Spotlight 공급자 '{p.Name}' 실패", ex);
                }
            }
            if (cts.IsCancellationRequested || !ReferenceEquals(cts, _cts) || !any) return;
            Updated?.Invoke(query, Arrange(query, all), true);
        }
        catch (OperationCanceledException)
        {
            // 새 입력으로 취소됨
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>
    /// 카테고리별 묶기. 검색어가 있으면 맨 위 "최상위 히트" = 우선순위(계산기 &gt; 앱 &gt; 설정 &gt; 파일)가 가장 높은 카테고리의 첫 항목
    /// (그 카테고리에서는 빠짐). 웹/Windows 검색은 머리글 없이 맨 아래.
    /// </summary>
    public static IReadOnlyList<SpotlightSection> Arrange(string query, IReadOnlyList<SpotlightItem> items)
    {
        var groups = items.GroupBy(i => i.Category).OrderBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.ToList());
        var sections = new List<SpotlightSection>();
        if (query.Trim().Length > 0)
        {
            var topCategory = groups.Keys
                .Where(k => k is not (SpotlightCategory.Fallback or SpotlightCategory.Recent))
                .OrderBy(k => k).Cast<SpotlightCategory?>().FirstOrDefault();
            if (topCategory is { } tc && groups[tc].Count > 0)
            {
                var top = groups[tc][0];
                groups[tc].RemoveAt(0);
                sections.Add(new SpotlightSection(SpotlightCategory.TopHit, "최상위 히트", new[] { top }));
            }
        }
        foreach (var (category, list) in groups.OrderBy(kv => kv.Key))
        {
            if (list.Count == 0) continue;
            sections.Add(new SpotlightSection(category, HeaderOf(category), list));
        }
        return sections;
    }

    public static string? HeaderOf(SpotlightCategory c) => c switch
    {
        SpotlightCategory.TopHit => "최상위 히트",
        SpotlightCategory.Calculator => "계산기",
        SpotlightCategory.Recent => "최근 사용",
        SpotlightCategory.Apps => "응용 프로그램",
        SpotlightCategory.Settings => "시스템 설정",
        SpotlightCategory.Folders => "폴더",
        SpotlightCategory.Documents => "문서",
        SpotlightCategory.Media => "사진·동영상·음악",
        SpotlightCategory.OtherFiles => "기타 파일",
        _ => null,
    };
}
