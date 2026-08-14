using AuditionModStudio.Core.AI;

namespace AuditionModStudio.App.AiStudio;

public sealed record AiStudioOperationOption(
    AiStudioOperation Operation, string Label, string Description, bool RequiresReference);

public sealed record AiStudioOption(string Value, string Label);

public sealed record AiStudioAspectOption(string Value, string Label, int Width, int Height);

public sealed record AiStudioJobPresentation(
    Guid JobId,
    string Operation,
    string State,
    long ReservedCredits,
    long? FinalCredits,
    DateTimeOffset CreatedAt,
    bool CanCancel)
{
    public static AiStudioJobPresentation From(AiStudioJobSummary job) => new(
        job.JobId,
        AiStudioOptions.GetOperationLabel(job.Operation),
        job.State switch
        {
            AiStudioJobState.Queued => "Đang chờ",
            AiStudioJobState.Processing => "Đang xử lý",
            AiStudioJobState.Completed => "Hoàn tất",
            AiStudioJobState.Failed => "Không thành công",
            AiStudioJobState.Cancelled => "Đã hủy",
            _ => "Chưa xác định"
        },
        job.ReservedCredits,
        job.FinalCredits,
        job.CreatedAt,
        job.CanCancel);
}

public static class AiStudioOptions
{
    public static IReadOnlyList<AiStudioOperationOption> Operations { get; } =
    [
        new(AiStudioOperation.Generate, "Tạo ảnh", "Tạo hình ảnh mới từ nội dung mô tả.", false),
        new(AiStudioOperation.Edit, "Chỉnh sửa ảnh nguồn", "Biến đổi Texture đang chọn.", true),
        new(AiStudioOperation.Inpaint, "Inpaint", "Tạo lại vùng Mask theo ảnh nguồn.", true),
        new(AiStudioOperation.Outpaint, "Outpaint", "Mở rộng Texture đang chọn.", true),
        new(AiStudioOperation.RemoveObject, "Xóa đối tượng", "Xóa nội dung bên trong vùng Mask.", true),
        new(AiStudioOperation.ReplaceObject, "Thay đối tượng", "Thay nội dung bên trong vùng Mask.", true),
        new(AiStudioOperation.Upscale, "Upscale", "Tăng chi tiết và giữ nguyên Texture đã chọn.", true),
    ];

    public static IReadOnlyList<AiStudioOption> Models { get; } =
    [
        new("server-default", "Mặc định"),
        new("balanced", "Cân bằng"),
        new("detail", "Chi tiết"),
    ];

    public static IReadOnlyList<AiStudioOption> Qualities { get; } =
    [
        new("standard", "Tiêu chuẩn"),
        new("high", "Cao"),
    ];

    public static IReadOnlyList<AiStudioAspectOption> Aspects { get; } =
    [
        new("square", "Vuông · 1:1", 512, 512),
        new("landscape", "Ngang · 3:2", 768, 512),
        new("portrait", "Dọc · 2:3", 512, 768),
    ];

    public static string GetOperationLabel(AiStudioOperation operation) =>
        Operations.FirstOrDefault(item => item.Operation == operation)?.Label ?? "Tác vụ AI";
}
