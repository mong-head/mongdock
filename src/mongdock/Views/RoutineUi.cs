using System.IO;
using System.Windows.Controls;
using Mongdock.Models;
using Mongdock.Services;

namespace Mongdock.Views;

/// <summary>
/// 루틴 (#24-A) 공용 동작 — 독·앱 모음 판·설정이 같이 씀: 실행, 끝내기 확인, 만들기(지금 화면 저장·직접), 편집, 지우기,
/// 독 고정·빼기, 루틴 메뉴, "루틴에 넣기 ▸", 다른 데스크톱 안내 카드. UI 스레드.
/// </summary>
internal static class RoutineUi
{
    /// <summary>NEW 배지 키 (판 루틴 줄·독 빈자리 "지금 화면을 루틴으로 저장…").</summary>
    public const string Badge = "routines";

    private static AppServices? _services;

    public static void Init(AppServices services)
    {
        _services = services;
        RoutineService.Init(services);
        if (SettingsService.RoutineRunsJustReset)
        {
            SettingsService.RoutineRunsJustReset = false;
            services.Settings.Save(); // 이관한 0 을 파일에도 바로 (저장 전에 꺼져도 다음 시작 때 다시 이관되지만 기다리지 않게)
        }
        RoutineTriggers.Init(services); // 더 보기: 시작·끝 조건, 함께 바꿀 것, 비슷하게 열면 묻기, 머문 시간
        RoutineService.ElsewhereShown += list => _ = ShowElsewhereAsync(services, list);
    }

    public static RoutineDef? Find(AppServices services, string? id) =>
        id is null ? null : services.Settings.Current.Routines.FirstOrDefault(r => r.Id == id);

    public static bool CanAdd(AppServices services) => services.Settings.Current.Routines.Count < RoutineDef.MaxRoutines;

    /// <summary>기본 이름 "루틴 1", "루틴 2", … (이미 있는 이름은 건너뜀).</summary>
    public static string NextName(AppServices services)
    {
        var names = services.Settings.Current.Routines.Select(r => r.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        for (int i = 1; ; i++)
        {
            string name = Loc.F($"루틴 {i}");
            if (!names.Contains(name)) return name;
        }
    }

    /// <summary>루틴을 눌렀을 때 일어난 일 (독이 짧은 말풍선으로 알림).</summary>
    public enum Click { Started, Opening, MovedToDesktop, AlreadyHere, NotStarted }

    /// <summary>
    /// 루틴 누름 (독·판·메뉴 "열기"·설정 공통): 이번 실행 창이 살아 있으면 새로 열지 않고 그 데스크톱으로 가서 창들을 앞으로,
    /// 여는 중이면 무시, 아니면 새로 엶.
    /// </summary>
    public static async Task<Click> RunOrFocusAsync(AppServices services, RoutineDef routine)
    {
        NewBadges.Used(Badge);
        if (RoutineService.IsRunning(routine.Id))
            return await RoutineService.FocusRunningAsync(routine.Id) ? Click.AlreadyHere : Click.MovedToDesktop;
        if (RoutineService.IsOpening(routine.Id)) return Click.Opening;
        return RoutineService.Run(routine) ? Click.Started : Click.NotStarted;
    }

    public static void Run(AppServices services, RoutineDef routine) => _ = RunOrFocusAsync(services, routine);

    /// <summary>끝내기 확인: "업무 시작 — 창 4개를 닫을까요?" + (이 루틴이 데스크톱을 만들었으면) "데스크톱도 닫기"(기본 켬).</summary>
    public static async void AskEnd(AppServices services, RoutineDef routine)
    {
        int n = RoutineService.OpenWindowCount(routine.Id);
        if (n == 0) return;
        string title = Loc.F($"{routine.Name} — 창 {n}개를 닫을까요?");
        string message = Loc.T("저장 안 한 작업은 각 앱이 물어봐요.");
        if (RoutineService.HasOwnDesktop(routine.Id))
        {
            var (ok, closeDesktop) = await ConfirmCardWindow.AskWithCheckAsync(services, title, message, Loc.T("닫기"), Loc.T("데스크톱도 닫기"), true);
            if (ok) RoutineService.End(routine, closeDesktop);
        }
        else if (await ConfirmCardWindow.AskAsync(services, title, message, Loc.T("닫기")))
            RoutineService.End(routine, false);
    }

    /// <summary>끝내기를 쓸 수 없을 때의 이유 (회색 메뉴의 툴팁). 쓸 수 있으면 null.</summary>
    public static string? EndDisabledReason(RoutineDef routine) =>
        RoutineService.OpenWindowCount(routine.Id) > 0 ? null
        : RoutineService.HasRunState(routine.Id) ? Loc.T("이번 루틴으로 연 창이 모두 닫혔어요")
        : Loc.T("몽독을 다시 시작해서 이번 루틴 창을 알 수 없어요");

    /// <summary>새 루틴을 목록에 넣고 저장 (최대 12개). 넣었으면 true.</summary>
    public static bool Add(AppServices services, RoutineDef routine)
    {
        var s = services.Settings.Current;
        if (s.Routines.Count >= RoutineDef.MaxRoutines) return false;
        if (string.IsNullOrEmpty(routine.Id)) routine.Id = "r-" + Guid.NewGuid().ToString("N")[..8];
        if (routine.Items.Count > RoutineDef.MaxItems) routine.Items.RemoveRange(RoutineDef.MaxItems, routine.Items.Count - RoutineDef.MaxItems);
        s.Routines.Add(routine);
        NewBadges.Used(Badge);
        services.Settings.Save();
        Log.Info($"루틴 추가: 항목 {routine.Items.Count}개{(routine.FromScreen ? " (지금 화면)" : "")}");
        return true;
    }

    public static async void AskDelete(AppServices services, RoutineDef routine)
    {
        if (!await ConfirmCardWindow.AskAsync(services, Loc.T("루틴 지우기"), Loc.F($"'{routine.Name}' 루틴을 지울까요? 앱과 파일은 그대로예요."), Loc.T("지우기"))) return;
        Delete(services, routine);
    }

    public static void Delete(AppServices services, RoutineDef routine)
    {
        var s = services.Settings.Current;
        RoutineTriggers.Restore(routine.Id); // 바꾼 소리 설정이 남아 있으면 되돌림 (지우면 되돌릴 버튼이 없어지므로)
        s.Routines.RemoveAll(r => r.Id == routine.Id);
        s.Pins.RemoveAll(p => p.Kind == PinKind.Routine && p.Target == routine.Id);
        services.Settings.Save();
        IconFiles.Cleanup(services.Settings); // 그 루틴 전용 그림
        Log.Info("루틴 지움");
    }

    public static bool IsPinned(AppServices services, RoutineDef routine) =>
        services.Settings.Current.Pins.Any(p => p.Kind == PinKind.Routine && p.Target == routine.Id);

    /// <summary>독에 고정 (at = 넣을 핀 자리, 없으면 끝 — 휴지통 앞).</summary>
    public static void PinToDock(AppServices services, RoutineDef routine, int? at = null)
    {
        var pins = services.Settings.Current.Pins;
        if (pins.Any(p => p.Kind == PinKind.Routine && p.Target == routine.Id)) return;
        var pin = new PinItem { Kind = PinKind.Routine, Target = routine.Id, Name = routine.Name, Id = routine.Id };
        if (at is { } i && i >= 0 && i <= pins.Count) pins.Insert(i, pin);
        else pins.Add(pin);
        services.Settings.Save();
        Log.Info("루틴 독에 고정");
    }

    public static void Unpin(AppServices services, RoutineDef routine)
    {
        services.Settings.Current.Pins.RemoveAll(p => p.Kind == PinKind.Routine && p.Target == routine.Id);
        services.Settings.Save();
    }

    /// <summary>"지금 화면으로 위치 다시 저장": 지금 떠 있는 그 앱 창 기준으로 항목마다 모니터·위치만.</summary>
    public static void RereadPositions(AppServices services, RoutineDef routine)
    {
        int n = RoutineService.RereadPlacements(routine.Items);
        services.Settings.Save();
        Log.Info($"루틴 위치 다시 저장: {n}/{routine.Items.Count}개");
    }

    /// <summary>
    /// 루틴 메뉴 (독 아이콘·판 칸 공통): 열기 / 루틴 끝내기 / 루틴 편집… / 지금 화면으로 위치 다시 저장 / 이름 바꾸기 / 독에 고정·독에서 빼기 / 루틴 지우기….
    /// before = 메뉴 항목을 누르기 전(판 닫기 등).
    /// </summary>
    public static void FillMenu(ContextMenu menu, AppServices services, RoutineDef routine, Action? before = null)
    {
        void Do(Action a) { before?.Invoke(); a(); }
        menu.Items.Add(DockMenus.Item(Loc.T("열기"), () => Do(() => Run(services, routine)), enabled: routine.Items.Count > 0));
        var end = DockMenus.Item(Loc.T("루틴 끝내기"), () => Do(() => AskEnd(services, routine)), enabled: EndDisabledReason(routine) is null);
        if (EndDisabledReason(routine) is { } why)
        {
            end.ToolTip = why;
            ToolTipService.SetShowOnDisabled(end, true);
        }
        menu.Items.Add(end);
        if (!RoutineService.IsRunning(routine.Id) && RoutineTriggers.HasRestore(routine.Id))
            menu.Items.Add(DockMenus.Item(Loc.T("설정만 되돌리기"), () => RoutineTriggers.Restore(routine.Id)));
        menu.Items.Add(new Separator());
        menu.Items.Add(DockMenus.Item(Loc.T("루틴 편집…"), () => Do(() => RoutineEditorWindow.Open(services, routine))));
        menu.Items.Add(DockMenus.Item(Loc.T("지금 화면으로 위치 다시 저장"), () => Do(() => RereadPositions(services, routine)), enabled: routine.Items.Count > 0));
        menu.Items.Add(DockMenus.Item(Loc.T("이름 바꾸기"), () => Do(() => RoutineEditorWindow.Open(services, routine, focusName: true))));
        menu.Items.Add(new Separator());
        menu.Items.Add(IsPinned(services, routine)
            ? DockMenus.Item(Loc.T("독에서 빼기"), () => Unpin(services, routine))
            : DockMenus.Item(Loc.T("독에 고정"), () => PinToDock(services, routine)));
        menu.Items.Add(DockMenus.Item(Loc.T("루틴 지우기…"), () => Do(() => AskDelete(services, routine))));
    }

    /// <summary>"루틴에 넣기 ▸" (루틴 목록 / 새 루틴…). 그 앱 창이 지금 떠 있으면 위치·모니터도 같이.</summary>
    public static MenuItem AddToRoutineMenu(AppServices services, Func<RoutineItem?> make, Action? before = null)
    {
        var parent = new MenuItem { Header = Loc.T("루틴에 넣기") };
        RoutineItem? Made()
        {
            var item = make();
            if (item is null) return null;
            var hwnd = RoutineService.WindowOf(item);
            if (hwnd != IntPtr.Zero) RoutineService.ReadPlacement(hwnd, item);
            return item;
        }
        foreach (var r in services.Settings.Current.Routines)
        {
            var routine = r;
            bool full = routine.Items.Count >= RoutineDef.MaxItems;
            parent.Items.Add(DockMenus.Item(routine.Name, () =>
            {
                before?.Invoke();
                if (Made() is not { } item) return;
                routine.Items.Add(item);
                services.Settings.Save();
                Log.Info("루틴에 넣기");
            }, enabled: !full));
        }
        if (services.Settings.Current.Routines.Count > 0) parent.Items.Add(new Separator());
        parent.Items.Add(DockMenus.Item(Loc.T("새 루틴…"), () =>
        {
            before?.Invoke();
            RoutineEditorWindow.OpenNew(services, Made());
        }, enabled: CanAdd(services)));
        return parent;
    }

    /// <summary>
    /// 앱 모음 판의 앱 → 루틴 항목. 시작 메뉴 바로 가기가 있으면 독 고정과 같이 바로 가기를 읽어(대상 exe + 인자 — Update.exe --processStart 같은 앱·크롬 프로필),
    /// 없으면 AppsFolder 키로 실행.
    /// </summary>
    public static RoutineItem ItemFromApp(AppServices services, AppEntry app)
    {
        if (app.Shortcut is { } lnk && File.Exists(lnk))
        {
            try
            {
                if (PinFactory.CreatePin(lnk, services.Settings) is { } pin && ItemFromPin(pin) is { Kind: RoutineItemKind.App } item)
                {
                    item.Name = app.Name;
                    return item;
                }
            }
            catch (Exception ex) { Log.Warn($"루틴: 바로 가기 읽기 실패 {ex.GetType().Name}"); }
        }
        return new RoutineItem { Kind = RoutineItemKind.App, Aumid = app.Key, Name = app.Name };
    }

    /// <summary>독 핀 → 루틴 항목 (exe·스토어 앱만).</summary>
    public static RoutineItem? ItemFromPin(PinItem pin) => pin.Kind switch
    {
        // 독의 Exe 종류 핀이 폴더·일반 파일·바로 가기이면 파일·폴더 항목 (연결된 앱으로 엶)
        PinKind.Exe when pin.Target.Length > 0 && !pin.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            => new RoutineItem { Kind = RoutineItemKind.Path, Target = pin.Target, Name = pin.Name },
        PinKind.Exe when pin.Target.Length > 0 => new RoutineItem { Kind = RoutineItemKind.App, Target = pin.Target, Args = string.IsNullOrWhiteSpace(pin.Arguments) ? null : pin.Arguments, Name = pin.Name },
        PinKind.Aumid when pin.Target.Length > 0 => new RoutineItem { Kind = RoutineItemKind.App, Aumid = pin.Target, Name = pin.Name },
        _ => null,
    };

    /// <summary>다른 데스크톱에 이미 켜져 있어 열지 않은 앱 안내 (한 번): [그 데스크톱으로 가기] = 첫 창의 데스크톱으로 가서 그 창을 앞으로.</summary>
    private static async Task ShowElsewhereAsync(AppServices services, List<(string Name, IntPtr Hwnd)> list)
    {
        try
        {
            string names = string.Join(" · ", list.Select(x => x.Name).Distinct().Take(5));
            bool? go = await ConfirmCardWindow.AskChoiceAsync(services, Loc.F($"이미 켜져 있는 앱 {list.Count}개는 원래 데스크톱에 있어요"),
                names, Loc.T("그 데스크톱으로 가기"), Loc.T("닫기"));
            if (go == true) await RoutineService.GoToWindowAsync(list[0].Hwnd);
        }
        catch (Exception ex) { Log.Error("루틴 안내 카드 실패", ex); }
    }
}
