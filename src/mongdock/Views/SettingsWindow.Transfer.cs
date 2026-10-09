using System.IO;
using System.Windows;
using System.Windows.Controls;
using Mongdock.Services;

namespace Mongdock.Views;

/// <summary>설정 → 일반 → "설정 옮기기": 내보내기·가져오기 (Services/SettingsTransfer). 결과는 그룹 아래 한 줄.</summary>
internal sealed partial class SettingsWindow
{
    /// <summary>마지막 내보내기·가져오기 결과 문구 (페이지를 다시 그려도 남게).</summary>
    private string? _transferMessage;
    private bool _transferBusy;

    private void BuildTransferSection(Panel body)
    {
        body.Children.Add(SectionTitle(Loc.T("설정 옮기기")));
        body.Children.Add(Group(
            Row(Loc.T("설정 내보내기"), Loc.T("독·상단바·알림·검색 설정과 독 아이콘을 .mongdock 파일 하나로 저장해요. 다른 PC 나 다시 설치할 때 가져오기로 불러와요."),
                ActionButton(Loc.T("내보내기…"), () => _ = ExportAsync())),
            Row(Loc.T("설정 가져오기"), Loc.T("내보낸 .mongdock 파일로 설정을 바꿔요. 바꾸기 전에 지금 설정은 백업해 둬요."),
                ActionButton(Loc.T("가져오기…"), () => _ = ImportAsync()))));
        if (!string.IsNullOrEmpty(_transferMessage))
        {
            body.Children.Add(new TextBlock
            {
                Text = _transferMessage,
                FontSize = 12,
                Foreground = _p.SubText,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, -8, 4, 16),
            });
        }
    }

    private void ShowTransferMessage(string text)
    {
        _transferMessage = text;
        QueueRebuild();
    }

    private async Task ExportAsync()
    {
        if (_transferBusy || _services.Settings is not SettingsService settings) return;
        try
        {
            bool includeUrls = false;
            if (_services.Calendars.Feeds.Count > 0)
            {
                // 캘린더 주소는 비밀 링크 — 기본은 빼고, 원하면 넣기 (DPAPI 원본은 다른 PC 에서 못 풀림)
                bool? choice = await ConfirmCardWindow.AskChoiceAsync(_services,
                    Loc.T("캘린더 주소도 넣을까요?"),
                    Loc.T("캘린더 주소는 비밀 링크라 이 파일을 가진 사람은 일정을 볼 수 있어요. 빼면 다른 PC 에서 캘린더만 다시 연결하면 돼요."),
                    Loc.T("주소 넣기"), Loc.T("빼기"));
                if (choice is null) return;
                includeUrls = choice == true;
            }
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = Loc.T("몽독 설정 내보내기"),
                FileName = Loc.F($"mongdock-설정-{DateTime.Now:yyyyMMdd}{SettingsTransfer.Extension}"),
                DefaultExt = SettingsTransfer.Extension,
                Filter = Loc.F($"몽독 설정 (*{SettingsTransfer.Extension})|*{SettingsTransfer.Extension}"),
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            };
            if (dialog.ShowDialog(this) != true) return;
            _transferBusy = true;
            var m = await Task.Run(() => SettingsTransfer.Export(dialog.FileName, settings, _services.Calendars, includeUrls));
            ShowTransferMessage(Loc.F($"내보냈어요: {Path.GetFileName(dialog.FileName)} — 독 앱 {m.PinCount}개, 아이콘 {m.IconCount}개, 캘린더 {m.CalendarCount}개") +
                                (m.CalendarCount > 0 ? (m.IncludesCalendarUrls ? Loc.T(" (주소 포함 — 파일을 조심히 다뤄 주세요)") : Loc.T(" (주소 뺌)")) : ""));
        }
        catch (Exception ex)
        {
            Log.Error("설정 내보내기 실패", ex);
            ShowTransferMessage(Loc.F($"내보내지 못했어요: {ex.Message}"));
        }
        finally
        {
            _transferBusy = false;
        }
    }

    private async Task ImportAsync()
    {
        if (_transferBusy || _services.Settings is not SettingsService settings) return;
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = Loc.T("몽독 설정 가져오기"),
                Filter = Loc.F($"몽독 설정 (*{SettingsTransfer.Extension})|*{SettingsTransfer.Extension}"),
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            };
            if (dialog.ShowDialog(this) != true) return;

            SettingsTransfer.Preview preview;
            try { preview = SettingsTransfer.ReadPreview(dialog.FileName); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or System.Text.Json.JsonException)
            {
                Log.Warn($"설정 가져오기: 읽기 실패 ({ex.GetType().Name})");
                ShowTransferMessage(ex is InvalidDataException ? ex.Message : Loc.T("몽독 설정 파일을 읽지 못했어요."));
                return;
            }

            var m = preview.Manifest;
            int withUrl = preview.Calendars.Count(c => !string.IsNullOrEmpty(c.Url));
            string cals = preview.Calendars.Count == 0 ? ""
                : withUrl == preview.Calendars.Count ? Loc.F($" · 캘린더 {preview.Calendars.Count}개")
                : Loc.F($" · 캘린더 {preview.Calendars.Count}개(주소 없는 {preview.Calendars.Count - withUrl}개는 다시 연결)");
            bool ok = await ConfirmCardWindow.AskAsync(_services,
                Loc.T("이 설정으로 바꿀까요?"),
                Loc.F($"v{m.AppVersion} · {m.Created:yyyy-MM-dd} 만듦 · 독 앱 {preview.PinNames.Count}개 · 아이콘 {m.IconCount}개{cals}. ") +
                Loc.T("지금 설정은 백업해 둬요. 자동 실행·알림 소리는 이 PC 설정 그대로예요."),
                Loc.T("가져오기"));
            if (!ok) return;

            _transferBusy = true;
            var r = await SettingsTransfer.ImportAsync(dialog.FileName, settings, _services.Calendars);
            ShowTransferMessage(Loc.F($"가져왔어요 — 독 앱 {r.Pins}개, 아이콘 {r.Icons}개") +
                                (r.CalendarsAdded > 0 ? Loc.F($", 캘린더 {r.CalendarsAdded}개 추가") : "") +
                                (r.CalendarsToReconnect.Count > 0 ? Loc.F($". 캘린더 {string.Join(", ", r.CalendarsToReconnect)} 은(는) 이 PC 에서 다시 연결해 주세요 (설정 → 캘린더)") : "") +
                                Loc.F($". 이전 설정 백업: settings.json{r.BackupSuffix}"));
        }
        catch (Exception ex)
        {
            Log.Error("설정 가져오기 실패", ex);
            ShowTransferMessage(Loc.F($"가져오지 못했어요: {ex.Message}"));
        }
        finally
        {
            _transferBusy = false;
        }
    }
}
