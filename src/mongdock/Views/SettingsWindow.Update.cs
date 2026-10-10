using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Mongdock.Services;

namespace Mongdock.Views;

/// <summary>설정 창 정보 페이지의 "업데이트" 구역: 수동 확인, 자동 확인 토글, 새 버전 카드(지금 업데이트·릴리스 노트·건너뛰기).</summary>
internal sealed partial class SettingsWindow
{
    private const double ProgressWidth = 180;

    private bool _updateHooked;
    /// <summary>이 창에서 "업데이트 확인" 을 눌렀는지 (결과 문구는 그때만).</summary>
    private bool _updateManualChecked;
    private Border? _updateFill;
    private TextBlock? _updateProgressText;

    /// <summary>설정 창을 "정보" 페이지로 열기 (업데이트 알림 클릭).</summary>
    public static void OpenAboutPage(AppServices services) => Open(services, Page.About);

    private void HookUpdates(UpdateService updates)
    {
        if (_updateHooked) return;
        _updateHooked = true;
        updates.Changed += OnUpdateChanged;
        updates.ProgressChanged += OnUpdateProgress;
        Closed += (_, _) =>
        {
            updates.Changed -= OnUpdateChanged;
            updates.ProgressChanged -= OnUpdateProgress;
        };
    }

    private void OnUpdateChanged(object? sender, EventArgs e)
    {
        if (_page == Page.About && !_closed) QueueRebuild();
    }

    private void OnUpdateProgress(object? sender, EventArgs e)
    {
        if (UpdateService.Instance is not { } u || _updateFill is null || _updateProgressText is null) return;
        _updateFill.Width = ProgressWidth * u.DownloadProgress;
        _updateProgressText.Text = ProgressText(u);
    }

    private static string ProgressText(UpdateService u)
    {
        long total = u.Pending?.SetupSize ?? 0;
        return total > 0
            ? Loc.F($"내려받는 중… {u.DownloadProgress:P0} ({u.DownloadedBytes / 1048576.0:0.0} / {total / 1048576.0:0.0} MB)")
            : Loc.T("내려받는 중…");
    }

    private void BuildUpdateSection(Panel body)
    {
        body.Children.Add(SectionTitle(Loc.T("업데이트")));
        if (UpdateService.Instance is not { } updates)
        {
            body.Children.Add(Group(Row(Loc.T("업데이트"), Loc.T("업데이트 확인을 사용할 수 없어요."), ActionButton(Loc.T("릴리스 페이지"),
                () => _services.Launcher.OpenFile(UpdateService.ReleasesPageUrl)))));
            return;
        }
        HookUpdates(updates);

        // 확인 상태 한 줄
        string status;
        if (updates.IsChecking) status = Loc.T("확인 중…");
        else if (_updateManualChecked && updates.LastResult == UpdateCheckResult.Failed) status = updates.LastError ?? Loc.T("확인하지 못했어요");
        else if (_updateManualChecked && updates.Pending is null && updates.LastResult is UpdateCheckResult.UpToDate) status = Loc.T("최신 버전이에요");
        else if (updates.Pending is { } p) status = Loc.F($"새 버전 v{p.VersionText} 이 있어요");
        else if (updates.LastChecked is { } t) status = Loc.F($"마지막 확인 {t:M월 d일 tt h:mm}");
        else status = Loc.T("아직 확인하지 않았어요");

        var check = ActionButton(updates.IsChecking ? Loc.T("확인 중…") : Loc.T("업데이트 확인"), async () =>
        {
            _updateManualChecked = true;
            await updates.CheckAsync(manual: true);
        });
        check.IsEnabled = !updates.IsChecking && !updates.IsDownloading && !updates.IsInstalling;

        body.Children.Add(Group(
            Row(Loc.F($"지금 버전 {VersionText()}"), status, check),
            Row(Loc.T("자동으로 업데이트 확인"), Loc.T("몽독을 켜고 1분 뒤, 그 뒤 12시간마다 GitHub 릴리스를 확인해요."),
                Toggle(_services.Settings.Current.CheckForUpdates, on => Commit(() => _services.Settings.Current.CheckForUpdates = on)))));

        if (updates.Pending is { } info)
            body.Children.Add(UpdateCard(updates, info));
    }

    /// <summary>새 버전 카드: 제목 · 릴리스 노트(최대 높이 + 얇은 스크롤바) · 버튼.</summary>
    private Border UpdateCard(UpdateService updates, UpdateInfo info)
    {
        var stack = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };
        stack.Children.Add(new TextBlock
        {
            Text = $"mongdock v{info.VersionText}",
            FontSize = 15,
            FontWeight = FontWeights.Bold,
        });
        stack.Children.Add(new TextBlock
        {
            Text = Loc.F($"지금 버전 {VersionText()} → 새 버전 {info.VersionText}"),
            FontSize = 11.5,
            Foreground = _p.SubText,
            Margin = new Thickness(0, 2, 0, 10),
        });

        string notes = UpdateService.NotesToText(info.Notes);
        if (notes.Length > 0)
        {
            var scroll = new ScrollViewer
            {
                MaxHeight = 170,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false,
                Content = new TextBlock
                {
                    Text = notes,
                    FontSize = 12.5,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 19,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    Margin = new Thickness(0, 0, 10, 0),
                },
            };
            if (TryFindResource("ThinScrollBar") is Style thin) scroll.Resources.Add(typeof(ScrollBar), thin);
            stack.Children.Add(new Border
            {
                Background = _p.Tile,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 9, 4, 9),
                Margin = new Thickness(0, 0, 0, 12),
                Child = scroll,
            });
        }

        bool canSelf = UpdateService.CanSelfUpdate(out string why);
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };

        if (updates.IsInstalling)
        {
            stack.Children.Add(Note(Loc.T("설치 프로그램을 실행했어요. 잠시 뒤 몽독이 꺼졌다가 새 버전으로 다시 켜져요.")));
        }
        else if (updates.IsDownloading)
        {
            var track = new Grid { Width = ProgressWidth, Height = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            track.Children.Add(new Border { Background = _p.Tile, CornerRadius = new CornerRadius(3) });
            _updateFill = new Border
            {
                Background = _p.AccentFill,
                CornerRadius = new CornerRadius(3),
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = ProgressWidth * updates.DownloadProgress,
            };
            track.Children.Add(_updateFill);
            _updateProgressText = new TextBlock { Text = ProgressText(updates), FontSize = 11.5, Foreground = _p.SubText, VerticalAlignment = VerticalAlignment.Center };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            row.Children.Add(track);
            row.Children.Add(_updateProgressText);
            stack.Children.Add(row);
            buttons.Children.Add(Spaced(ActionButton(Loc.T("취소"), updates.CancelDownload)));
        }
        else
        {
            _updateFill = null;
            _updateProgressText = null;
            if (canSelf && info.SetupUrl is not null)
            {
                // 설치가 끝나지 않았으면(2분 안에 몽독이 안 꺼짐·setup 실패) DownloadError 와 함께 "다시 시도"
                var now = ActionButton(updates.DownloadError is null ? Loc.T("지금 업데이트") : Loc.T("다시 시도"), async () =>
                {
                    if (await updates.DownloadAndInstallAsync(info)) QueueRebuild();
                });
                now.Background = _p.Accent;
                now.Foreground = _p.AccentText;
                buttons.Children.Add(Spaced(now));
            }
            else
            {
                stack.Children.Add(Note(canSelf ? Loc.T("이 릴리스에는 설치 파일이 없어요. 릴리스 페이지에서 받아 주세요.") : why));
                var page = ActionButton(Loc.T("릴리스 페이지 열기"), () => _services.Launcher.OpenFile(UpdateService.SafeReleaseUrl(info.HtmlUrl)));
                page.Background = _p.Accent;
                page.Foreground = _p.AccentText;
                buttons.Children.Add(Spaced(page));
            }
            if (updates.DownloadError is { } err)
                stack.Children.Add(Note(err));
        }

        if (canSelf && info.SetupUrl is not null)
            buttons.Children.Add(Spaced(ActionButton(Loc.T("릴리스 노트 보기"), () => _services.Launcher.OpenFile(UpdateService.SafeReleaseUrl(info.HtmlUrl)))));
        if (!updates.IsDownloading && !updates.IsInstalling)
            buttons.Children.Add(Spaced(ActionButton(Loc.T("이 버전 건너뛰기"), () => updates.Skip(info))));
        stack.Children.Add(buttons);

        return new Border
        {
            Background = _p.GroupBackground,
            BorderBrush = _p.SoftAccentLine,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 0, 0, 16),
            Child = stack,
        };
    }

    private static Button Spaced(Button b)
    {
        b.Margin = new Thickness(0, 0, 8, 0);
        return b;
    }

    private TextBlock Note(string text) => new()
    {
        Text = text,
        FontSize = 11.5,
        Foreground = _p.SubText,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 10),
    };
}
