using AuditIt.Api.Models;
using Xunit;

namespace AuditIt.Api.Tests;

public class RenterLinkDataTests
{
    [Fact]
    public void CalendarEventsExposeRenterIdForTenantLinks()
    {
        var renterId = Guid.NewGuid();
        var dto = new RentalCalendarEventDto
        {
            Id = "rental-period-1",
            Kind = RentalCalendarEventKind.RentalPeriod,
            RentalId = Guid.NewGuid(),
            RenterId = renterId,
            RenterName = "Tenant"
        };

        Assert.Equal(renterId, dto.RenterId);
    }

    [Fact]
    public void FinanceDetailsExposeRenterIdForTenantLinks()
    {
        var renterId = Guid.NewGuid();
        var dto = new FinanceReportDetailDto
        {
            RentalId = Guid.NewGuid(),
            RentalNumber = "R20260617-0001",
            RenterId = renterId,
            RenterName = "Tenant"
        };

        Assert.Equal(renterId, dto.RenterId);
    }
}
