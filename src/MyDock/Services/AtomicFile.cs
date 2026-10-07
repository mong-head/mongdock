using System.IO;
using System.Text;

namespace MyDock.Services;

/// <summary>
/// 작은 설정/상태 파일을 원자적으로 저장: 같은 폴더의 임시 파일에 다 쓴 뒤 File.Replace(있을 때)/File.Move 로 바꿔 끼움.
/// 쓰는 도중 꺼지거나 크래시해도 기존 파일이 반쯤 잘린 채로 남지 않는다. 실패하면 예외 (호출자가 로그).
/// </summary>
internal static class AtomicFile
{
    public static void WriteAllText(string path, string content)
    {
        string dir = Path.GetDirectoryName(path) ?? ".";
        Directory.CreateDirectory(dir);
        string tmp = Path.Combine(dir, Path.GetFileName(path) + "." + Environment.ProcessId + ".tmp");
        try
        {
            File.WriteAllText(tmp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (File.Exists(path))
            {
                try
                {
                    File.Replace(tmp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                    return;
                }
                catch (IOException)
                {
                    // 다른 볼륨·특수 파일 시스템 등 Replace 가 안 되는 경우 → 덮어쓰기 이동
                }
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public static void WriteAllLines(string path, IEnumerable<string> lines) =>
        WriteAllText(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);
}
