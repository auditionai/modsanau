using System.Runtime.InteropServices;

namespace AuditionModStudio.App.Bootstrap;

internal static partial class WindowsErrorDialog
{
    private const uint ErrorIcon = 0x00000010;
    private const uint OkButton = 0x00000000;

    public static void ShowFatalStartupError()
    {
        Show(
            "Audition AI Mod Studio không thể khởi động. " +
            "Vui lòng kiểm tra thư mục Logs trong LocalAppData để biết thêm chi tiết.");
    }

    public static void ShowUnexpectedError()
    {
        Show(
            "Ứng dụng gặp lỗi nghiêm trọng và cần đóng. " +
            "Thông tin kỹ thuật đã được ghi vào thư mục Logs.");
    }

    private static void Show(string message)
    {
        _ = MessageBox(0, message, "Audition AI Mod Studio", OkButton | ErrorIcon);
    }

    [LibraryImport(
        "user32.dll",
        EntryPoint = "MessageBoxW",
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(
        nint windowHandle,
        string text,
        string caption,
        uint type);
}
