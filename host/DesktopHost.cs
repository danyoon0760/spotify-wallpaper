using static SpotifyWallpaper.Native;

namespace SpotifyWallpaper;

// 창을 바탕화면 아이콘 "뒤"에 붙인다.
//  - Windows 11 24H2+ (raised desktop): SHELLDLL_DefView가 Progman의 자식.
//    레이어드 자식 창(alpha 255)으로 Progman에 붙이고 DefView 바로 아래에 둔다.
//  - 그 이전: 0x052C로 생성된 WorkerW(아이콘 창 다음 형제)에 붙인다.
internal sealed class DesktopHost
{
    public IntPtr Parent { get; private set; }
    public IntPtr DefView { get; private set; }
    public bool Raised { get; private set; }

    public bool Attach(IntPtr hwnd, Rectangle screenBounds)
    {
        IntPtr progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero) return false;

        // Explorer에게 배경 레이어(WorkerW)를 만들게 요청
        SendMessageTimeout(progman, 0x052C, new IntPtr(0xD), new IntPtr(0x1), SMTO_NORMAL, 1000, out _);
        SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, SMTO_NORMAL, 1000, out _);

        for (int attempt = 0; attempt < 20; attempt++)
        {
            IntPtr defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView != IntPtr.Zero)
            {
                // 24H2+ 구조
                IntPtr worker = FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
                if (worker != IntPtr.Zero || attempt > 5)
                {
                    Raised = true;
                    DefView = defView;
                    Parent = progman;
                    MakeChild(hwnd, layered: true);
                    SetParent(hwnd, progman);
                    Place(hwnd, screenBounds, defView);
                    return true;
                }
            }
            else
            {
                IntPtr worker = FindClassicWorkerW();
                if (worker != IntPtr.Zero)
                {
                    Raised = false;
                    Parent = worker;
                    MakeChild(hwnd, layered: false);
                    SetParent(hwnd, worker);
                    Place(hwnd, screenBounds, IntPtr.Zero);
                    return true;
                }
            }
            Thread.Sleep(100);
        }
        return false;
    }

    // 배경화면 창들(모니터마다 하나)이 아이콘(DefView) 바로 뒤에 연달아 있는지 확인하고,
    // 순서가 깨졌을 때만 다시 줄 세운다. 창끼리의 순서는 상관없다.
    // (창마다 "내가 바로 뒤인가"를 따로 확인하면 창이 둘 이상일 때 매번 서로 자리를 뺏어 깜빡인다)
    public static void EnsureBelowIcons(IntPtr defView, IReadOnlyList<IntPtr> windows)
    {
        if (defView == IntPtr.Zero || windows.Count == 0) return;
        var ours = new HashSet<IntPtr>(windows);
        int consecutive = 0;
        for (IntPtr h = GetWindow(defView, GW_HWNDNEXT); h != IntPtr.Zero && ours.Contains(h); h = GetWindow(h, GW_HWNDNEXT))
            consecutive++;
        if (consecutive == ours.Count) return;

        Log.Write("z-order broken → restack wallpaper windows below icons");
        IntPtr after = defView;
        foreach (var w in windows)
        {
            SetWindowPos(w, after, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            after = w;
        }
    }

    public bool IsParentAlive => Parent != IntPtr.Zero && IsWindow(Parent);

    // 붙어 있던 창이 사라지거나 숨은 뒤 바탕화면에 마지막 프레임이 남지 않도록 현재 배경화면을 다시 적용
    public static void RefreshWallpaper()
    {
        var sb = new System.Text.StringBuilder(520);
        if (SystemParametersInfo(SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0) && sb.Length > 0)
            SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, sb, 0);
    }

    static IntPtr FindClassicWorkerW()
    {
        IntPtr result = IntPtr.Zero;
        EnumWindows((top, _) =>
        {
            if (FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                result = FindWindowEx(IntPtr.Zero, top, "WorkerW", null);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    static void MakeChild(IntPtr hwnd, bool layered)
    {
        long style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        style = (style & ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME)) | WS_CHILD;
        SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style));

        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        ex = (ex | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW;
        if (layered) ex |= WS_EX_LAYERED;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
        if (layered) SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
    }

    void Place(IntPtr hwnd, Rectangle screen, IntPtr insertAfter)
    {
        // 부모 창 좌표계 기준으로 변환 (부모는 가상 화면 전체를 덮음)
        GetWindowRect(Parent, out RECT pr);
        int x = screen.Left - pr.Left;
        int y = screen.Top - pr.Top;
        uint flags = SWP_NOACTIVATE | SWP_SHOWWINDOW | (insertAfter == IntPtr.Zero ? SWP_NOZORDER : 0);
        SetWindowPos(hwnd, insertAfter, x, y, screen.Width, screen.Height, flags);
    }
}
