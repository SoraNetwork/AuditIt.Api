using AuditIt.Api.Models;

namespace AuditIt.Api.Services
{
    public interface ISfExpressService
    {
        Task<IReadOnlyList<SfRouteQueryResult>> QueryRoutesAsync(
            IReadOnlyList<SfRouteQueryItem> items,
            bool forceRefresh,
            CancellationToken ct = default);
    }
}
