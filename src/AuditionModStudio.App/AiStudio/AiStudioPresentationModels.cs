using AuditionModStudio.Core.AI;

namespace AuditionModStudio.App.AiStudio;

public sealed record AiStudioOperationOption(
    AiStudioOperation Operation, string Label, string Description, bool RequiresReference);

public sealed record AiStudioOption(
    string Value,
    string Label,
    string Description = "",
    string PriceText = "Giá được xác nhận khi tạo ảnh");

public sealed record AiStudioAspectOption(string Value, string Label, int Width, int Height);

public sealed class AiModelSettingViewModel
{
    public AiModelSettingViewModel(string key, string label, IReadOnlyList<AiStudioOption> options)
    {
        Key = key;
        Label = label;
        Options = options;
        Selected = options.FirstOrDefault() ?? new AiStudioOption("", "");
    }

    public string Key { get; }
    public string Label { get; }
    public IReadOnlyList<AiStudioOption> Options { get; }
    public AiStudioOption Selected { get; set; }
}

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
        new(AiStudioOperation.Generate, "Tạo ảnh mới", "Tạo hình ảnh từ mô tả của bạn.", false),
        new(AiStudioOperation.Edit, "Chỉnh từ Texture", "Dùng Texture đang chọn làm ảnh tham chiếu.", true),
        new(AiStudioOperation.Inpaint, "Tạo lại vùng đã tô", "Áp dụng lên vùng Mask đã vẽ.", true),
        new(AiStudioOperation.Outpaint, "Mở rộng ảnh", "Mở rộng vùng ngoài Texture đang chọn.", true),
        new(AiStudioOperation.RemoveObject, "Xóa đối tượng", "Xóa phần nằm trong vùng Mask.", true),
        new(AiStudioOperation.ReplaceObject, "Thay đối tượng", "Thay phần nằm trong vùng Mask theo mô tả.", true),
        new(AiStudioOperation.Upscale, "Tăng độ nét", "Tăng chi tiết cho Texture đang chọn.", true),
    ];

    // These options keep the page usable while the secure catalog is loading.
    public static IReadOnlyList<AiStudioOption> Models { get; } =
    [
        new("server-default", "Model mặc định", "Dịch vụ sẽ chọn model phù hợp.", "Giá được xác nhận khi tạo ảnh"),
    ];

    public static IReadOnlyList<AiStudioOption> Qualities { get; } =
    [
        new("standard", "Tiêu chuẩn"),
        new("high", "Chất lượng cao"),
    ];

    public static IReadOnlyList<AiStudioAspectOption> Aspects { get; } =
    [
        new("square", "Vuông · 1:1", 1024, 1024),
        new("landscape", "Ngang · 3:2", 1536, 1024),
        new("portrait", "Dọc · 2:3", 1024, 1536),
    ];

    public static string GetOperationLabel(AiStudioOperation operation) =>
        Operations.FirstOrDefault(item => item.Operation == operation)?.Label ?? "Tác vụ AI";
}
