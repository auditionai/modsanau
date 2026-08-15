using AuditionModStudio.Core.Auth;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AuditionModStudio.App.Authentication;

public sealed partial class LoginPage : Page
{
    private readonly IAuthenticationService _authentication;
    private readonly IDesktopAccessService _desktopAccess;
    private int _busy;
    private int _startupChecked;

    public LoginPage(IAuthenticationService authentication, IDesktopAccessService desktopAccess)
    {
        _authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
        _desktopAccess = desktopAccess ?? throw new ArgumentNullException(nameof(desktopAccess));
        InitializeComponent();
    }

    public event EventHandler? AccessGranted;

    public void ShowGateFailure(string diagnosticCode)
    {
        ShowError(diagnosticCode is "DEVICE_INACTIVE" or "DEVICE_SESSION_REVOKED" or "DEVICE_SESSION_INACTIVE"
            ? "Thiết bị này đã bị khóa hoặc thu hồi."
            : "Phiên thiết bị không còn hợp lệ. Hãy kiểm tra mạng và đăng nhập lại.");
    }

    public void ResetForSignIn()
    {
        PasswordInput.Password = string.Empty;
        StatusBar.IsOpen = false;
        EmailInput.Focus(FocusState.Programmatic);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Interlocked.Exchange(ref _startupChecked, 1) != 0) return;
        SetBusy(true);
        try
        {
            var profile = await _authentication.GetProfileAsync();
            if (!profile.Succeeded && profile.FailureReason == AuthenticationFailureReason.Rejected)
            {
                var refreshed = await _authentication.RefreshSessionAsync();
                if (refreshed.Succeeded) profile = await _authentication.GetProfileAsync();
            }
            if (profile.Succeeded) await CompleteDeviceGateAsync();
        }
        finally { SetBusy(false); }
    }

    private async void OnSignInClicked(object sender, RoutedEventArgs e)
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0) return;
        SetBusy(true);
        try
        {
            AuthEmail email;
            AuthPassword password;
            try
            {
                email = new AuthEmail(EmailInput.Text);
                password = new AuthPassword(PasswordInput.Password);
            }
            catch (ArgumentException)
            {
                ShowError("Vui lòng nhập Gmail hợp lệ và mật khẩu.");
                return;
            }

            var result = await _authentication.SignInAsync(email, password);
            if (!result.Succeeded)
            {
                ShowError("Không đăng nhập được. Hãy kiểm tra mật khẩu và xác minh Gmail trước.");
                return;
            }
            await CompleteDeviceGateAsync();
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
            SetBusy(false);
        }
    }

    private async Task CompleteDeviceGateAsync()
    {
        var access = await _desktopAccess.EnsureAccessAsync();
        if (!access.Succeeded)
        {
            ShowError(access.DiagnosticCode switch
            {
                "EMAIL_CONFIRMATION_REQUIRED" => "Bạn cần xác minh Gmail trước khi dùng ứng dụng.",
                "USER_INACTIVE" => "Tài khoản đã bị tạm khóa.",
                "DEVICE_INACTIVE" or "DEVICE_SESSION_REVOKED" or "DEVICE_SESSION_INACTIVE" => "Thiết bị này đã bị khóa hoặc thu hồi.",
                "DESKTOP_CONFIGURATION_UNAVAILABLE" => "Ứng dụng chưa được cấu hình kết nối Supabase.",
                _ => "Không thể đăng ký thiết bị. Hãy kiểm tra mạng rồi thử lại.",
            });
            return;
        }
        StatusBar.IsOpen = false;
        AccessGranted?.Invoke(this, EventArgs.Empty);
    }

    private void SetBusy(bool busy)
    {
        BusyIndicator.IsActive = busy;
        SignInButton.IsEnabled = !busy;
        EmailInput.IsEnabled = !busy;
        PasswordInput.IsEnabled = !busy;
    }

    private void ShowError(string message)
    {
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }
}
