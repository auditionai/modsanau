using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AuditionModStudio.App.Account;

namespace AuditionModStudio.App.Shell;

public sealed class AppShellViewModel : INotifyPropertyChanged
{
    private static readonly ImmutableArray<ShellNavigationItem> RouteCatalog =
    [
        new(AppRoute.Home, "Trang chủ", "\uE80F", "Trang chủ", "Chọn game và loại Mod để bắt đầu dự án."),
        new(AppRoute.Projects, "Dự án", "\uE8B7", "Dự án", "Mở và quản lý các dự án Mod Audition."),
        new(AppRoute.AiStudio, "AI Studio", "\uE945", "AI Studio", "Tạo hình ảnh bằng AI và xem lại lịch sử tạo."),
        new(AppRoute.ImageEditor, "Trình chỉnh sửa ảnh", "\uE91B", "Trình chỉnh sửa ảnh", "Cắt và đổi kích thước Texture theo đúng thông số DDS."),
        new(AppRoute.Account, "Tài khoản", "\uE77B", "Tài khoản", "Xem hồ sơ, số dư Credits và hoạt động gần đây."),
        new(AppRoute.Settings, "Cài đặt", "\uE713", "Cài đặt", "Tùy chỉnh giao diện và xem thông tin ứng dụng.")
    ];

    private ShellNavigationItem _currentItem = RouteCatalog[0];
    private readonly AccountViewModel? _account;

    public AppShellViewModel(AccountViewModel? account = null)
    {
        _account = account;
        if (_account is not null) _account.PropertyChanged += OnAccountPropertyChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ImmutableArray<ShellNavigationItem> NavigationItems => RouteCatalog;

    public AppRoute CurrentRoute => _currentItem.Route;

    public string CurrentTitle => _currentItem.Title;

    public string CurrentDescription => _currentItem.Description;

    public string AccountStatus => _account?.HasSnapshot == true
        ? _account.DisplayName != "Chưa cung cấp" ? _account.DisplayName : _account.Email
        : "Chưa đăng nhập";

    public string CreditsStatus => _account?.HasSnapshot == true
        ? $"{_account.AvailableCredits} Credits" : "Chưa có dữ liệu Credits";

    public string NotificationStatus => "Không có thông báo";

    public string ConnectionStatus => _account?.IsLoading == true ? "Đang kết nối"
        : _account?.HasSnapshot == true ? "Trực tuyến" : "Ngoại tuyến";

    public bool Navigate(AppRoute route)
    {
        var nextItem = RouteCatalog.FirstOrDefault(item => item.Route == route);
        if (nextItem is null || nextItem == _currentItem)
        {
            return false;
        }

        _currentItem = nextItem;
        OnPropertyChanged(nameof(CurrentRoute));
        OnPropertyChanged(nameof(CurrentTitle));
        OnPropertyChanged(nameof(CurrentDescription));
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void OnAccountPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(AccountViewModel.HasSnapshot) or nameof(AccountViewModel.DisplayName)
            or nameof(AccountViewModel.Email)) OnPropertyChanged(nameof(AccountStatus));
        if (args.PropertyName is nameof(AccountViewModel.HasSnapshot) or nameof(AccountViewModel.AvailableCredits))
            OnPropertyChanged(nameof(CreditsStatus));
        if (args.PropertyName is nameof(AccountViewModel.HasSnapshot) or nameof(AccountViewModel.IsLoading))
            OnPropertyChanged(nameof(ConnectionStatus));
    }
}
