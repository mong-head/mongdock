using System.Windows.Controls;
using System.Windows.Media;
using MyDock.Models;
using MyDock.Services;

namespace MyDock.Views;

/// <summary>
/// 새 버전 알림 UI 연결: 처음 발견한 버전은 몽독 배너 한 번(클릭 = 설정 → 정보), 로고·트레이 메뉴 맨 위 항목.
/// 실제 다운로드·설치는 설정 창 정보 페이지(SettingsWindow.Update.cs)에서.
/// </summary>
internal static class UpdateUi
{
    /// <summary>배너 카드 Id (윈도우 알림 Id 와 겹치지 않게 음수).</summary>
    private const long BannerId = -7300;

    /// <summary>App 시작 때 한 번: Available → 배너. Dispose 하면 구독 해제.</summary>
    public static IDisposable Attach(AppServices services, UpdateService updates) => new Hook(services, updates);

    private sealed class Hook : IDisposable
    {
        private readonly AppServices _services;
        private readonly UpdateService _updates;

        public Hook(AppServices services, UpdateService updates)
        {
            _services = services;
            _updates = updates;
            _updates.Available += OnAvailable;
        }

        private void OnAvailable(object? sender, UpdateInfo info)
        {
            if (WhatsNew.Parse(_services.Settings.Current.NotifiedUpdateVersion) == info.Version) return;
            var item = new NotificationItem(BannerId, "", AppInfo.Name, "mongdock 업데이트 있음 · v" + info.VersionText,
                new[] { "눌러서 새 버전 정보를 보고 설치할 수 있어요." }, DateTime.Now, null, null, false, null);
            if (NotificationBannerWindow.ShowCustom(item, AppIcon(_services), () => OpenAbout(_services)))
                _updates.MarkNotified(info);
        }

        public void Dispose() => _updates.Available -= OnAvailable;
    }

    /// <summary>몽독 실행 파일 아이콘 (독 아이콘 스타일 그대로).</summary>
    private static ImageSource? AppIcon(AppServices services)
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe is null) return null;
            return services.Icons.GetIcon(new PinItem { Name = AppInfo.Name, Kind = PinKind.Exe, Target = exe }, services.Settings.Current.Dock.IconStyle);
        }
        catch (Exception ex)
        {
            Log.Warn($"몽독 아이콘 읽기 실패: {ex.Message}");
            return null;
        }
    }

    /// <summary>설정 창을 정보 페이지(업데이트 카드)로 열기.</summary>
    public static void OpenAbout(AppServices services) => SettingsWindow.OpenAboutPage(services);

    /// <summary>새 버전이 있으면 메뉴 맨 위에 "업데이트 있음 — v0.3.1 설치…" + 구분선. 없으면 아무것도 안 넣음.</summary>
    public static void AddMenuItems(ItemsControl menu, AppServices services)
    {
        if (UpdateService.Instance?.Pending is not { } info) return;
        var item = DockMenus.Item($"업데이트 있음 — v{info.VersionText} 설치…", () => OpenAbout(services));
        item.FontWeight = System.Windows.FontWeights.SemiBold;
        menu.Items.Insert(0, item);
        menu.Items.Insert(1, new Separator());
    }

    /// <summary>상단바 로고 배지를 보여야 하는지.</summary>
    public static bool HasPending => UpdateService.Instance?.Pending is not null;
}
