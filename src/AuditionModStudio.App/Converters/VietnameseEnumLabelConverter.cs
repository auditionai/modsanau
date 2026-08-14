using AuditionModStudio.App.Workspace;
using AuditionModStudio.Core.Images;
using Microsoft.UI.Xaml.Data;

namespace AuditionModStudio.App.Converters;

public sealed class VietnameseEnumLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        WorkspaceMappingFilter.All => "Tất cả",
        WorkspaceMappingFilter.ManifestMapped => "Đã ánh xạ",
        WorkspaceMappingFilter.Unmapped => "Chưa ánh xạ",
        TextureStatusFilter.All => "Tất cả",
        TextureStatusFilter.Modified => "Đã chỉnh sửa",
        TextureStatusFilter.Original => "Bản gốc",
        TextureStatusFilter.Invalid => "Không hợp lệ",
        TextureStatusFilter.AI => "AI",
        TextureSizeFilter.All => "Tất cả",
        TextureSizeFilter.Small => "Nhỏ",
        TextureSizeFilter.Medium => "Vừa",
        TextureSizeFilter.Large => "Lớn",
        TextureAlphaFilter.All => "Tất cả",
        TextureAlphaFilter.HasAlpha => "Có Alpha",
        TextureAlphaFilter.NoAlpha => "Không có Alpha",
        AiMaskBrushMode.Paint => "Tô Mask",
        AiMaskBrushMode.Erase => "Xóa Mask",
        _ => value?.ToString() ?? string.Empty
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
