using System.IO;
using System.Text;
using Mongdock.Models;

namespace Mongdock.Services;

/// <summary>
/// MyDockFinder 의 ico.ini (UTF-16 LE, 읽기 전용) → 핀 목록.
/// - 섹션 순서대로 변환, appname "launchpad|" → Special "launchpad"
/// - UWP=1 → Kind=Aumid, Target = filepath(소문자로 저장된 AUMID) 를 shell:AppsFolder 열거로 대소문자 복원
/// - delimiter=1 → Separator (맨 끝 구분선은 버림)
/// - icopath → ImportIcon 으로 복사, UWP 항목은 icopath 가 없으면 &lt;ini 폴더&gt;\runpng\&lt;tag 소문자&gt;.png
/// - realpath(버전 포함 WindowsApps 경로)는 절대 저장하지 않음
/// 원본 ini 는 읽기만 한다 (FileAccess.Read).
/// </summary>
public sealed class MyDockFinderImporter
{
    private readonly ISettingsService _settings;

    public MyDockFinderImporter(ISettingsService settings)
    {
        _settings = settings;
    }

    public List<PinItem> Import(string iniPath)
    {
        var pins = new List<PinItem>();
        try
        {
            if (!File.Exists(iniPath))
            {
                Log.Warn($"ico.ini 없음: {iniPath}");
                return pins;
            }
            string iniDir = Path.GetDirectoryName(Path.GetFullPath(iniPath)) ?? "";
            foreach (var (section, kv) in ParseIni(iniPath))
            {
                try
                {
                    var pin = Convert(section, kv, iniDir);
                    if (pin is not null) pins.Add(pin);
                }
                catch (Exception ex)
                {
                    Log.Error($"ico.ini [{section}] 변환 실패", ex);
                }
            }

            while (pins.Count > 0 && pins[^1].Kind == PinKind.Separator) pins.RemoveAt(pins.Count - 1);
            Log.Info($"ico.ini 가져오기: {pins.Count}개 ({string.Join(", ", pins.Select(p => $"{p.Kind}:{p.Name}"))})");
        }
        catch (Exception ex)
        {
            Log.Error($"ico.ini 가져오기 실패: {iniPath}", ex);
        }
        return pins;
    }

    /// <summary>UTF-16 ini 를 (섹션, 키→값) 목록으로 파일 순서대로 파싱. 키는 대소문자 무시.</summary>
    internal static List<(string Section, Dictionary<string, string> Values)> ParseIni(string path)
    {
        var result = new List<(string, Dictionary<string, string>)>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        // BOM 이 있으면 그것을 따르고, 없으면 UTF-16 LE 로 간주
        using var sr = new StreamReader(fs, Encoding.Unicode, detectEncodingFromByteOrderMarks: true);
        Dictionary<string, string>? cur = null;
        string? line;
        while ((line = sr.ReadLine()) is not null)
        {
            line = line.Trim().TrimStart('﻿');
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                cur = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                result.Add((line[1..^1].Trim(), cur));
                continue;
            }
            if (cur is null) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            cur[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return result;
    }

    private PinItem? Convert(string section, Dictionary<string, string> kv, string iniDir)
    {
        string Get(string k) => kv.TryGetValue(k, out var v) ? v.Trim() : "";

        if (Get("delimiter") == "1")
            return new PinItem { Name = "", Kind = PinKind.Separator, Target = "" };

        string tag = Get("tag");
        string appname = Get("appname");

        // 내장 항목: appname 이 "xxx|" 형태
        if (appname.EndsWith('|'))
        {
            string special = appname.TrimEnd('|').Trim().ToLowerInvariant();
            if (special == "launchpad")
                return new PinItem { Name = tag.Length > 0 ? tag : "Launchpad", Kind = PinKind.Special, Target = "launchpad" };
            Log.Warn($"ico.ini [{section}] 지원하지 않는 내장 항목 '{appname}' → 건너뜀");
            return null;
        }

        string filepath = Get("filepath");
        string icopath = Get("icopath");
        bool uwp = Get("UWP") == "1";

        PinItem pin;
        if (uwp)
        {
            string aumid = filepath;
            if (!aumid.Contains('!'))
            {
                // filepath 가 AUMID 가 아니면 realpath 에서 패키지 패밀리만 뽑아 AUMID 를 찾는다 (realpath 자체는 저장 안 함)
                string? family = AppsFolder.FamilyFromWindowsAppsPath(Get("realpath"));
                aumid = (family is null ? null : AppsFolder.FindAumidByFamily(family)) ?? "";
            }
            if (aumid.Length == 0)
            {
                Log.Warn($"ico.ini [{section}] UWP 항목의 AUMID 를 알 수 없음 → 건너뜀");
                return null;
            }
            string? restored = AppsFolder.RestoreAumidCase(aumid);
            if (restored is null) Log.Warn($"AUMID 대소문자 복원 실패(설치 안 됨?) → 그대로 사용: {aumid}");
            pin = new PinItem
            {
                Name = tag.Length > 0 ? tag : (AppsFolder.GetAppDisplayName(restored ?? aumid) ?? aumid),
                Kind = PinKind.Aumid,
                Target = restored ?? aumid,
            };
            if (icopath.Length == 0 && tag.Length > 0)
            {
                string runpng = Path.Combine(iniDir, "runpng", tag.ToLowerInvariant() + ".png");
                if (File.Exists(runpng)) icopath = runpng;
            }
        }
        else
        {
            string target = filepath.Length > 0 ? filepath : Get("realpath");
            if (target.Length == 0)
            {
                Log.Warn($"ico.ini [{section}] 경로 없음 → 건너뜀");
                return null;
            }
            if (AppsFolder.IsWindowsAppsPath(target))
            {
                // UWP 표시가 없어도 WindowsApps 경로면 AUMID 로 변환 (버전 경로 저장 금지)
                string? family = AppsFolder.FamilyFromWindowsAppsPath(target);
                string? aumid = family is null ? null : AppsFolder.FindAumidByFamily(family);
                if (aumid is null)
                {
                    Log.Warn($"ico.ini [{section}] WindowsApps 경로인데 AUMID 를 못 찾음 → 건너뜀: {target}");
                    return null;
                }
                pin = new PinItem { Name = tag.Length > 0 ? tag : aumid, Kind = PinKind.Aumid, Target = aumid };
            }
            else
            {
                string real = AppsFolder.RestorePathCase(target); // ini 는 소문자 → 실제 대소문자로 (표시용)
                pin = new PinItem
                {
                    Name = tag.Length > 0 ? tag : Path.GetFileNameWithoutExtension(real),
                    Kind = PinKind.Exe,
                    Target = real,
                };
            }
        }

        if (icopath.Length > 0)
        {
            try
            {
                string src = AppsFolder.RestorePathCase(icopath);
                if (File.Exists(src)) pin.IconPath = _settings.ImportIcon(src);
                else Log.Warn($"ico.ini [{section}] 아이콘 파일 없음: {icopath}");
            }
            catch (Exception ex)
            {
                Log.Error($"ico.ini [{section}] 아이콘 복사 실패: {icopath}", ex);
            }
        }
        return pin;
    }
}
