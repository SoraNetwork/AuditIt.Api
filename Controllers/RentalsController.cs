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

        [HttpGet("{id:guid}")]
        [RequirePermission(PermissionCodes.RentalView)]
        public async Task<ActionResult<RentalDto>> Get(Guid id)
        {
            var rental = await _rentals.GetByIdAsync(id);
            return rental == null ? NotFound() : Ok(rental);
        }

        [HttpPost]
        [RequirePermission(PermissionCodes.RentalCreate)]
        public async Task<ActionResult<RentalDto>> Create([FromBody] CreateRentalDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);
            var (rental, error) = await _rentals.CreateAsync(dto, CurrentUser());
            if (error != null) return BadRequest(error);
            return CreatedAtAction(nameof(Get), new { id = rental!.Id }, rental);
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

        private string? CurrentUser() => User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    }
}
