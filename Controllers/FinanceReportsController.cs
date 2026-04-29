using AuditIt.Api.Data;
using AuditIt.Api.Models;
using AuditIt.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AuditIt.Api.Controllers
{
    [ApiController]
    [Route("api/finance-reports")]
    [Authorize]
    [RequirePermission(PermissionCodes.FinanceReportView)]
    public class FinanceReportsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;

        public FinanceReportsController(ApplicationDbContext context)
        {
            _context = context;
        }

        [HttpGet("summary")]
        public async Task<ActionResult<FinanceReportSummaryDto>> Summary([FromQuery] FinanceReportQueryParameters query)
        {
            var (from, to) = ResolveRange(query);
            var rentals = await BuildQuery(from, to).ToListAsync();
            var details = rentals.Select(ToDetail).ToList();

            return Ok(new FinanceReportSummaryDto
            {
                From = from,
                To = to,
                RentalCount = rentals.Count,
                ActiveRentalCount = rentals.Count(r => r.Status is RentalStatus.Pending or RentalStatus.Active or RentalStatus.Overdue),
                ClosedRentalCount = rentals.Count(r => r.Status is RentalStatus.Returned or RentalStatus.Cancelled),
                TotalOrderAmount = details.Sum(d => d.TotalPrice),
                TotalDeposit = details.Sum(d => d.Deposit),
                TotalShippingFee = details.Sum(d => d.TotalShippingFee),
                TotalOtherFee = details.Sum(d => d.OtherFee),
                AccountedAmount = details.Sum(d => d.AccountedAmount),
                Statuses = details
                    .GroupBy(d => d.Status)
                    .OrderBy(g => g.Key)
                    .Select(g => new FinanceReportStatusSummaryDto
                    {
                        Status = g.Key,
                        Count = g.Count(),
                        TotalOrderAmount = g.Sum(d => d.TotalPrice),
                        AccountedAmount = g.Sum(d => d.AccountedAmount)
                    })
                    .ToList()
            });
        }

        [HttpGet("details")]
        public async Task<ActionResult<IEnumerable<FinanceReportDetailDto>>> Details([FromQuery] FinanceReportQueryParameters query)
        {
            var (from, to) = ResolveRange(query);
            var rentals = await BuildQuery(from, to)
                .OrderByDescending(r => r.CreatedAt)
                .ToListAsync();

            return Ok(rentals.Select(ToDetail));
        }

        private IQueryable<Rental> BuildQuery(DateTime from, DateTime to) =>
            _context.Rentals
                .Include(r => r.Renter)
                .Include(r => r.Items)
                .Include(r => r.Shipments)
                .Where(r => r.CreatedAt >= from && r.CreatedAt <= to);

        private static (DateTime from, DateTime to) ResolveRange(FinanceReportQueryParameters query)
        {
            var today = DateTime.UtcNow.Date;
            var from = (query.From ?? new DateTime(today.Year, today.Month, 1)).Date;
            var to = (query.To ?? today).Date.AddDays(1).AddTicks(-1);

            if (to < from)
            {
                (from, to) = (to.Date, from.Date.AddDays(1).AddTicks(-1));
            }

            if ((to - from).TotalDays > 370)
            {
                to = from.AddDays(370).AddTicks(-1);
            }

            return (from, to);
        }

        private static FinanceReportDetailDto ToDetail(Rental rental)
        {
            var totalShippingFee = rental.Shipments.Sum(s => s.ShippingFee ?? 0);
            return new FinanceReportDetailDto
            {
                RentalId = rental.Id,
                RentalNumber = rental.RentalNumber,
                Status = rental.Status,
                RenterName = rental.Renter?.Name,
                AssignedTo = rental.AssignedTo,
                StartDate = rental.StartDate,
                ExpectedEndDate = rental.ExpectedEndDate,
                CreatedAt = rental.CreatedAt,
                TotalPrice = rental.TotalPrice,
                Deposit = rental.Deposit ?? 0,
                TotalShippingFee = totalShippingFee,
                OtherFee = rental.OtherFee,
                AccountedAmount = rental.TotalPrice - rental.OtherFee - totalShippingFee,
                ItemCount = rental.Items.Count,
                PlatformOrderNo = rental.PlatformOrderNo
            };
        }
    }
}
