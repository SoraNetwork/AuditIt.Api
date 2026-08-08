using System.Security.Claims;
using AuditIt.Api.Models;
using AuditIt.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuditIt.Api.Controllers;

[ApiController]
[Route("api/shipment-reminder-settings")]
[Authorize]
public class ShipmentReminderSettingsController : ControllerBase
{
    private readonly IShipmentReminderService _shipmentReminders;

    public ShipmentReminderSettingsController(IShipmentReminderService shipmentReminders)
    {
        _shipmentReminders = shipmentReminders;
    }

    [HttpGet]
    [RequirePermission(PermissionCodes.ShipmentReminderManage)]
    public async Task<ActionResult<ShipmentReminderSettingsDto>> Get(CancellationToken ct) =>
        Ok(await _shipmentReminders.GetSettingsAsync(ct));

    [HttpGet("recipients")]
    [RequirePermission(PermissionCodes.ShipmentReminderManage)]
    public async Task<ActionResult<IReadOnlyList<ShipmentReminderRecipientDto>>> ListRecipients(CancellationToken ct) =>
        Ok(await _shipmentReminders.ListActiveRecipientsAsync(ct));

    [HttpGet("sms-templates")]
    [RequirePermission(PermissionCodes.ShipmentReminderManage)]
    public async Task<ActionResult<IReadOnlyList<AliyunSmsTemplateDto>>> ListSmsTemplates(CancellationToken ct)
    {
        try
        {
            return Ok(await _shipmentReminders.ListSmsTemplatesAsync(ct));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPut]
    [RequirePermission(PermissionCodes.ShipmentReminderManage)]
    public async Task<ActionResult<ShipmentReminderSettingsDto>> Update(
        [FromBody] UpdateShipmentReminderSettingsDto dto,
        CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        try
        {
            return Ok(await _shipmentReminders.UpdateSettingsAsync(dto, CurrentUser(), ct));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("test")]
    [RequirePermission(PermissionCodes.ShipmentReminderManage)]
    public async Task<ActionResult<IReadOnlyList<ShipmentReminderTestResultDto>>> Test(
        [FromBody] TestShipmentReminderDto dto,
        CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        try
        {
            return Ok(await _shipmentReminders.SendTestAsync(dto, ct));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    private string? CurrentUser() => User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
}
