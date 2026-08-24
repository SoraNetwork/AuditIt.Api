using System.ComponentModel.DataAnnotations;

namespace AuditIt.Api.Models
{
    public class SfDeliveryEstimateRequestDto
    {
        [Required]
        [StringLength(500)]
        public string DestinationAddress { get; set; } = string.Empty;

        public List<int> SourceWarehouseIds { get; set; } = new();

        // 具体物品模式下由服务端重新解析所属仓库，避免前端只传一个来源地址。
        public List<string> ItemIds { get; set; } = new();

        public DateTime? StartDate { get; set; }

        [Range(0.01, 100000)]
        public decimal Weight { get; set; } = 2.5m;
    }

    public class SfDeliveryEstimateQuery
    {
        public string DestinationAddress { get; init; } = string.Empty;
        public DateTime StartDate { get; init; }
        public decimal Weight { get; init; } = 2.5m;
        public IReadOnlyList<SfDeliveryEstimateSource> Sources { get; init; } = Array.Empty<SfDeliveryEstimateSource>();
    }

    public class SfDeliveryEstimateSource
    {
        public int WarehouseId { get; init; }
        public string WarehouseName { get; init; } = string.Empty;
        public string Address { get; init; } = string.Empty;
    }

    public class SfDeliveryEstimateResultDto
    {
        public string DestinationAddress { get; init; } = string.Empty;
        public SfParsedAddressDto Destination { get; init; } = new();
        public decimal Weight { get; init; }
        public DateTime ConsignedTime { get; init; }
        public DateTime TargetDeliveryTime { get; init; }
        public List<SfDeliveryEstimateWarehouseDto> Warehouses { get; init; } = new();
    }

    public class SfDeliveryEstimateWarehouseDto
    {
        public int WarehouseId { get; init; }
        public string WarehouseName { get; init; } = string.Empty;
        public string Address { get; init; } = string.Empty;
        public SfParsedAddressDto Source { get; init; } = new();
        public string? Error { get; init; }
        public List<SfDeliveryProductDto> Products { get; init; } = new();
    }

    public class SfParsedAddressDto
    {
        public string? Province { get; init; }
        public string? City { get; init; }
        public string? District { get; init; }
    }

    public class SfDeliveryProductDto
    {
        public string BusinessType { get; init; } = string.Empty;
        public string BusinessTypeDesc { get; init; } = string.Empty;
        public string? DeliverTime { get; init; }
        public decimal? Fee { get; init; }
        public string? SearchPrice { get; init; }
        public string? CloseTime { get; init; }
        public DateTime? DeliveryTime { get; init; }
        public int? DeliveryDays { get; init; }
        public DateTime? PlannedDeliveryTime { get; init; }
        public DateTime? LatestShipTime { get; init; }
        public DateTime ConsignedTime { get; init; }
    }
}
