using System.Security.Claims;
using AuditIt.Api.Models;
using AuditIt.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuditIt.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class RentalsController : ControllerBase
    {
        private readonly IRentalService _rentals;

        public RentalsController(IRentalService rentals)
        {
            _rentals = rentals;
        }

        [HttpGet]
        [RequirePermission(PermissionCodes.RentalView)]
        public async Task<ActionResult<object>> List([FromQuery] RentalQueryParameters query)
        {
            var (items, total) = await _rentals.ListAsync(query);
            return Ok(new { items, total });
        }

        [HttpGet("calendar")]
        [RequirePermission(PermissionCodes.RentalView)]
        public async Task<ActionResult<IEnumerable<RentalCalendarEventDto>>> Calendar([FromQuery] RentalCalendarQueryParameters query)
        {
            return Ok(await _rentals.GetCalendarAsync(
                query,
                CurrentUser(),
                User.HasPermission(PermissionCodes.ReminderView),
                User.HasPermission(PermissionCodes.ReminderDismissAny)));
        }

        [HttpGet("{id:guid}")]
        [RequirePermission(PermissionCodes.RentalView)]
        public async Task<ActionResult<RentalDto>> Get(Guid id)
        {
            var rental = await _rentals.GetByIdAsync(id);
            return rental == null ? NotFound() : Ok(rental);
        }

        [HttpGet("{id:guid}/sf-routes")]
        [RequirePermission(PermissionCodes.RentalView)]
        public async Task<ActionResult<SfRouteSyncResultDto>> SfRoutes(Guid id, [FromQuery] bool refresh = false, CancellationToken ct = default)
        {
            var (result, error) = await _rentals.SyncSfRoutesAsync(id, refresh, CurrentUser(), ct);
            if (error != null) return BadRequest(error);
            return Ok(result);
        }

        [HttpPost("sf-routes/refresh-pending")]
        [RequirePermission(PermissionCodes.RentalShip)]
        public async Task<ActionResult<SfPendingRouteRefreshResultDto>> RefreshPendingSfRoutes(CancellationToken ct = default)
        {
            return Ok(await _rentals.SyncPendingSfRoutesAsync(CurrentUser(), ct));
        }

        [HttpPost]
        [RequirePermission(PermissionCodes.RentalCreate)]
        public async Task<ActionResult<RentalDto>> Create([FromBody] CreateRentalDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);
            var result = await _rentals.CreateAsync(dto, CurrentUser());
            if (result.Conflict != null) return Conflict(result.Conflict);
            if (result.Error != null) return BadRequest(result.Error);
            return CreatedAtAction(nameof(Get), new { id = result.Rental!.Id }, result.Rental);
        }

        [HttpPut("{id:guid}")]
        [RequirePermission(PermissionCodes.RentalUpdate)]
        public async Task<ActionResult<RentalDto>> Update(Guid id, [FromBody] UpdateRentalDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);
            var (rental, error) = await _rentals.UpdateAsync(id, dto, CurrentUser());
            if (error != null) return BadRequest(error);
            return Ok(rental);
        }

        [HttpPost("{id:guid}/ship")]
        [RequirePermission(PermissionCodes.RentalShip)]
        public async Task<ActionResult<RentalDto>> Ship(Guid id, [FromBody] CreateShipmentDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);
            var (rental, error) = await _rentals.AddShipmentAsync(id, dto, CurrentUser());
            if (error != null) return BadRequest(error);
            return Ok(rental);
        }

        [HttpPost("{id:guid}/shipments/{shipmentId:int}/deliver")]
        [RequirePermission(PermissionCodes.RentalShip)]
        public async Task<ActionResult<RentalDto>> Deliver(Guid id, int shipmentId, [FromBody] DeliverShipmentDto dto)
        {
            var (rental, error) = await _rentals.MarkDeliveredAsync(id, shipmentId, dto, CurrentUser());
            if (error != null) return BadRequest(error);
            return Ok(rental);
        }

        [HttpPost("{id:guid}/return")]
        [RequirePermission(PermissionCodes.RentalReturn)]
        public async Task<ActionResult<RentalDto>> Return(Guid id, [FromBody] ReturnRentalDto dto)
        {
            var (rental, error) = await _rentals.ReturnAsync(id, dto, CurrentUser());
            if (error != null) return BadRequest(error);
            return Ok(rental);
        }

        [HttpPost("{id:guid}/cancel")]
        [RequirePermission(PermissionCodes.RentalCancel)]
        public async Task<ActionResult<RentalDto>> Cancel(Guid id, [FromBody] CancelRentalDto dto)
        {
            var (rental, error) = await _rentals.CancelAsync(id, dto, CurrentUser());
            if (error != null) return BadRequest(error);
            return Ok(rental);
        }

        [HttpPut("{id:guid}/items/bulk")]
        [RequirePermission(PermissionCodes.RentalUpdate)]
        public async Task<ActionResult<RentalDto>> BulkUpdateItems(Guid id, [FromBody] BulkUpdateRentalItemsDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);
            var (rental, error) = await _rentals.BulkUpdateItemsAsync(id, dto, CurrentUser());
            if (error != null) return BadRequest(error);
            return Ok(rental);
        }

        [HttpPut("{id:guid}/items")]
        [RequirePermission(PermissionCodes.RentalUpdate)]
        public async Task<ActionResult<RentalDto>> UpdateItems(Guid id, [FromBody] UpdateRentalItemsDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);
            var result = await _rentals.UpdateRentalItemsAsync(id, dto, CurrentUser());
            if (result.Conflict != null) return Conflict(result.Conflict);
            if (result.Error != null) return BadRequest(result.Error);
            return Ok(result.Rental);
        }

        private string? CurrentUser() => User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    }
}
