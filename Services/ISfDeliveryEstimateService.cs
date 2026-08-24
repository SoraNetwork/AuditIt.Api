using AuditIt.Api.Models;

namespace AuditIt.Api.Services
{
    public interface ISfDeliveryEstimateService
    {
        Task<SfDeliveryEstimateResultDto> QueryAsync(
            SfDeliveryEstimateQuery query,
            CancellationToken ct = default);
    }
}
