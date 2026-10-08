using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mongdock.Services;

namespace Mongdock.Views;

/// <summary>
/// 몽독 앱 아이콘 (Assets/mongdock.ico, tools/make-icon/make-icon.ps1 로 생성 — 16·20·24·32·40·48·64·128·256).
/// 창 제목 줄/Alt+Tab 용 <see cref="WindowIcon"/>, 트레이용 <see cref="CreateTrayIcon"/>.
/// </summary>
internal static class AppIcon
{
    private static readonly Uri IcoUri = new("pack://application:,,,/Assets/mongdock.ico", UriKind.Absolute);
    private static ImageSource? _window;
    private static bool _windowFailed;

    /// <summary>Window.Icon 에 넣을 이미지. 멀티 사이즈 ico 프레임이라 WPF 가 작은/큰 아이콘을 DPI 에 맞게 고른다. 실패하면 null (exe 기본 아이콘).</summary>
    public static ImageSource? WindowIcon
    {
        get
        {
            if (_window is not null || _windowFailed) return _window;
            try
            {
                var frame = BitmapFrame.Create(IcoUri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                frame.Freeze();
                _window = frame;
            }
            catch (Exception ex)
            {
                _windowFailed = true;
                Log.Error("앱 아이콘 불러오기 실패", ex);
            }
            return _window;
        }
    }

    /// <summary>창에 앱 아이콘 적용 (불러오기 실패면 그대로 둠).</summary>
    public static void Apply(Window window)
    {
        if (WindowIcon is ImageSource icon) window.Icon = icon;
    }

    /// <summary>
    /// 지정 픽셀 크기에 가장 맞는 ico 항목으로 System.Drawing.Icon 생성 (트레이용 16/20/24/32 …). 실패하면 null.
    /// 호출자가 Dispose.
    /// </summary>
    public static System.Drawing.Icon? CreateTrayIcon(int sizePx)
    {
        try
        {
            var info = Application.GetResourceStream(IcoUri);
            if (info is null) return null;
            using var stream = info.Stream;
            return new System.Drawing.Icon(stream, sizePx, sizePx);
        }
        catch (Exception ex)
        {
            Log.Error("트레이 아이콘 불러오기 실패", ex);
            return null;
        }
    }
}
