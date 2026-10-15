using System.Windows;
using System.Windows.Controls;
using Mongdock.Services;

namespace Mongdock.Views;

/// <summary>스토어판 정보 페이지의 "업데이트" 구역 (GitHub 업데이트 구역 자리): "Microsoft Store 에서 업데이트돼요" + [스토어에서 업데이트 확인] + 새 버전이면 [업데이트].</summary>
internal sealed partial class SettingsWindow
{
    private bool _storeUpdateHooked;

    private void BuildStoreUpdateSection(Panel body, StoreUpdateService updates)
    {
        if (!_storeUpdateHooked)
        {
            _storeUpdateHooked = true;
            updates.Changed += OnStoreUpdateChanged;
            Closed += (_, _) => updates.Changed -= OnStoreUpdateChanged;
        }

        body.Children.Add(SectionTitle(Loc.T("업데이트")));
        string status = updates.State switch
        {
            StoreUpdateState.Checking => Loc.T("확인 중…"),
            StoreUpdateState.UpToDate => Loc.T("최신 버전이에요"),
            StoreUpdateState.Available => Loc.F($"새 버전 v{VersionOrMark(updates)} 이 있어요"),
            StoreUpdateState.Installing => Loc.T("업데이트하는 중…"),
            StoreUpdateState.Failed => Loc.T("확인하지 못했어요"),
            _ => Loc.T("Microsoft Store 에서 업데이트돼요"),
        };
        var check = ActionButton(updates.State == StoreUpdateState.Checking ? Loc.T("확인 중…") : Loc.T("스토어에서 업데이트 확인"),
            () => _ = updates.CheckAsync(manual: true));
        check.IsEnabled = updates.State is not (StoreUpdateState.Checking or StoreUpdateState.Installing);

        var rows = new List<UIElement> { Row(Loc.F($"지금 버전 {VersionText()}"), status, check) };
        if (updates.HasPending)
        {
            var install = ActionButton(Loc.T("업데이트"), () => _ = updates.InstallAsync());
            install.IsEnabled = updates.State == StoreUpdateState.Available;
            rows.Add(Row(Loc.F($"새 버전 v{VersionOrMark(updates)}"),
                Loc.T("Microsoft Store 가 내려받아 설치해요. 설치하는 동안 몽독이 잠깐 꺼졌다가 다시 켜져요."), install));
        }
        else if (updates.State == StoreUpdateState.Failed)
        {
            rows.Add(Row(Loc.T("Microsoft Store"), Loc.T("스토어 앱의 몽독 페이지에서 직접 업데이트할 수도 있어요."),
                ActionButton(Loc.T("스토어 열기"), StoreUpdateService.OpenStorePage)));
        }
        else
        {
            rows.Add(Row(Loc.T("자동 업데이트"), Loc.T("Microsoft Store 가 알아서 업데이트해요. 몽독도 켜고 1분 뒤와 하루 한 번 새 버전을 확인해요."),
                new TextBlock()));
        }
        body.Children.Add(Group(rows.ToArray()));
    }

    private static string VersionOrMark(StoreUpdateService u) => u.PendingVersion?.ToString(3) ?? "?";

    private void OnStoreUpdateChanged(object? sender, EventArgs e)
    {
        if (_page == Page.About && !_closed) QueueRebuild();
    }
}
