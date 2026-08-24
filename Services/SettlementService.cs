using System.Globalization;
using System.Text.Json;
using AuditIt.Api.Data;
using AuditIt.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AuditIt.Api.Services
{
    public class SettlementService : ISettlementService
    {
        private const int SettingsId = 1;

        private readonly ApplicationDbContext _context;
        private readonly IDingTalkService _dingTalkService;
        private readonly ILogger<SettlementService> _logger;

        public SettlementService(
            ApplicationDbContext context,
            IDingTalkService dingTalkService,
            ILogger<SettlementService> logger)
        {
            _context = context;
            _dingTalkService = dingTalkService;
            _logger = logger;
        }

        public async Task<SettlementSettingDto> GetSettingsAsync(CancellationToken ct = default)
        {
            var settings = await GetSettingsEntityAsync(ct);
            return ToDto(settings);
        }

        public async Task<(SettlementSettingDto? settings, string? error)> UpdateSettingsAsync(
            UpdateSettlementSettingDto dto,
            string? currentUser,
            CancellationToken ct = default)
        {
            var totalPercent = dto.TechnicianPercent
                + dto.CreatorPercent
                + dto.ShipperPercent
                + dto.ItemOwnerPercent;
            if (totalPercent > 100m)
            {
                return (null, "结算比例合计不能超过 100%。");
            }

            var settings = await _context.SettlementSettings.FirstOrDefaultAsync(s => s.Id == SettingsId, ct);
            if (settings == null)
            {
                settings = new SettlementSetting { Id = SettingsId };
                _context.SettlementSettings.Add(settings);
            }

            settings.TechnicianPercent = dto.TechnicianPercent;
            settings.CreatorPercent = dto.CreatorPercent;
            settings.ShipperPercent = dto.ShipperPercent;
            settings.ItemOwnerPercent = dto.ItemOwnerPercent;
            settings.DefaultPaymentAccount = NormalizeNullableText(dto.DefaultPaymentAccount);
            if (dto.PaymentAccountPresets != null)
            {
                settings.PaymentAccountPresetsJson = SerializePaymentAccountPresets(dto.PaymentAccountPresets);
            }
            settings.UpdatedAt = DateTime.UtcNow;
            settings.UpdatedBy = currentUser;

            await _context.SaveChangesAsync(ct);
            return (ToDto(settings), null);
        }

        public async Task<SettlementPreviewDto?> GetPreviewAsync(Guid rentalId, CancellationToken ct = default)
        {
            var rental = await LoadRentalAsync(rentalId, ct);
            if (rental == null)
            {
                return null;
            }

            var settings = await GetSettingsEntityAsync(ct);
            var shipperSourceShipments = await LoadShipperSourceShipmentsAsync(rental, ct);
            return BuildPreview(rental, settings, shipperSourceShipments);
        }

        public async Task<List<SettlementPreviewDto>> GetPreviewsAsync(List<Rental> rentals, CancellationToken ct = default)
        {
            var settings = await GetSettingsEntityAsync(ct);
            var result = new List<SettlementPreviewDto>();
            foreach (var rental in rentals)
            {
                var shipperSourceShipments = await LoadShipperSourceShipmentsAsync(rental, ct);
                result.Add(BuildPreview(rental, settings, shipperSourceShipments));
            }
            return result;
        }

        public async Task<(SettlementPreviewDto? preview, string? error)> SendForRentalAsync(
            Guid rentalId,
            string? currentUser,
            bool force = false,
            CancellationToken ct = default)
        {
            var rental = await LoadRentalAsync(rentalId, ct);
            if (rental == null)
            {
                return (null, "租赁单不存在。");
            }

            var settings = await GetSettingsEntityAsync(ct);
            var shipperSourceShipments = await LoadShipperSourceShipmentsAsync(rental, ct);
            var preview = BuildPreview(rental, settings, shipperSourceShipments);
            if (!preview.CanSend)
            {
                return (preview, preview.IneligibleReason ?? "当前租赁单不能发送结算信息。");
            }

            if (!force && rental.SettlementNotifiedAt.HasValue)
            {
                return (preview, null);
            }

            var msgUuid = force
                ? $"settlement-manual-{rental.Id}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"
                : $"settlement-{rental.Id}-{rental.Status}";

            var sent = await _dingTalkService.SendRobotMarkdownAsync(
                $"结算单 {rental.RentalNumber}",
                preview.MarkdownText,
                msgUuid,
                ct);

            if (!sent)
            {
                return (preview, "钉钉机器人 Webhook 未配置或发送失败。");
            }

            rental.SettlementNotifiedAt = DateTime.UtcNow;
            rental.SettlementNotifiedStatus = FormatStatus(rental.Status);
            rental.UpdatedBy = currentUser ?? rental.UpdatedBy;
            await _context.SaveChangesAsync(ct);

            preview.SettlementNotifiedAt = rental.SettlementNotifiedAt;
            preview.SettlementNotifiedStatus = rental.SettlementNotifiedStatus;
            return (preview, null);
        }

        public async Task TrySendForRentalAsync(Guid rentalId, string? currentUser, CancellationToken ct = default)
        {
            try
            {
                await SendForRentalAsync(rentalId, currentUser, force: false, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send settlement message for rental {RentalId}.", rentalId);
            }
        }

        private async Task<Rental?> LoadRentalAsync(Guid rentalId, CancellationToken ct)
        {
            return await _context.Rentals
                .Include(r => r.Items)
                    .ThenInclude(ri => ri.Item)
                .Include(r => r.Shipments)
                .FirstOrDefaultAsync(r => r.Id == rentalId, ct);
        }

        private async Task<IReadOnlyList<RentalShipment>> LoadShipperSourceShipmentsAsync(Rental rental, CancellationToken ct)
        {
            var currentOutboundShipments = rental.Shipments
                .Where(s => s.Direction == ShipmentDirection.Outbound)
                .OrderBy(s => s.Id)
                .ToList();
            if (currentOutboundShipments.Count > 0)
            {
                return currentOutboundShipments;
            }

            var visitedRentalIds = new HashSet<Guid> { rental.Id };
            var sourceRentalId = rental.RenewedFromRentalId;
            while (sourceRentalId.HasValue && visitedRentalIds.Add(sourceRentalId.Value))
            {
                var lookupRentalId = sourceRentalId.Value;
                var sourceOutboundShipments = await _context.RentalShipments
                    .AsNoTracking()
                    .Where(s => s.RentalId == lookupRentalId && s.Direction == ShipmentDirection.Outbound)
                    .OrderBy(s => s.Id)
                    .ToListAsync(ct);
                if (sourceOutboundShipments.Count > 0)
                {
                    return sourceOutboundShipments;
                }

                sourceRentalId = await _context.Rentals
                    .AsNoTracking()
                    .Where(r => r.Id == lookupRentalId)
                    .Select(r => r.RenewedFromRentalId)
                    .FirstOrDefaultAsync(ct);
            }

            return Array.Empty<RentalShipment>();
        }

        private async Task<SettlementSetting> GetSettingsEntityAsync(CancellationToken ct)
        {
            return await _context.SettlementSettings.FirstOrDefaultAsync(s => s.Id == SettingsId, ct)
                ?? new SettlementSetting { Id = SettingsId };
        }

        private static SettlementPreviewDto BuildPreview(
            Rental rental,
            SettlementSetting settings,
            IReadOnlyList<RentalShipment> shipperSourceShipments)
        {
            var accountedAmount = AccountedAmount(rental);
            var technicianAmount = PercentAmount(accountedAmount, settings.TechnicianPercent);
            var creatorAmount = PercentAmount(accountedAmount, settings.CreatorPercent);
            var ownerPool = PercentAmount(accountedAmount, settings.ItemOwnerPercent);
            var shipperShares = BuildShipperShares(rental.SenderName, shipperSourceShipments, accountedAmount, settings.ShipperPercent);
            var shipperAmount = shipperShares.Sum(s => s.Amount);
            var ownerShares = SettlementOwnerShareCalculator.BuildOwnerShares(rental, accountedAmount, settings.ItemOwnerPercent);
            var ineligibleReason = ResolveIneligibleReason(rental);
            var markdown = BuildSettlementMarkdown(
                rental,
                settings,
                accountedAmount,
                technicianAmount,
                creatorAmount,
                shipperShares,
                ownerShares);

            return new SettlementPreviewDto
            {
                RentalId = rental.Id,
                RentalNumber = rental.RentalNumber,
                Status = rental.Status,
                PaymentAccount = rental.PaymentAccount,
                TotalPrice = rental.TotalPrice,
                AccountedAmount = accountedAmount,
                TechnicianPercent = settings.TechnicianPercent,
                TechnicianAmount = technicianAmount,
                CreatorPercent = settings.CreatorPercent,
                CreatorAmount = string.IsNullOrWhiteSpace(rental.CreatedBy) ? 0m : creatorAmount,
                CreatorName = string.IsNullOrWhiteSpace(rental.CreatedBy) ? null : rental.CreatedBy.Trim(),
                ShipperPercent = settings.ShipperPercent,
                ShipperAmount = shipperAmount,
                ShipperShares = shipperShares
                    .Select(i => new SettlementShipperShareDto { ShipperName = i.ShipperName, Amount = i.Amount })
                    .ToList(),
                ItemOwnerPercent = settings.ItemOwnerPercent,
                ItemOwnerAmount = ownerPool,
                OwnerShares = ownerShares
                    .Select(i => new SettlementOwnerShareDto
                    {
                        OwnerName = i.OwnerName,
                        ItemShortId = i.ItemShortId,
                        ItemName = i.ItemName,
                        Amount = i.Amount
                    })
                    .ToList(),
                MarkdownText = markdown,
                CanSend = ineligibleReason == null,
                IneligibleReason = ineligibleReason,
                SettlementNotifiedAt = rental.SettlementNotifiedAt,
                SettlementNotifiedStatus = rental.SettlementNotifiedStatus
            };
        }

        private static string? ResolveIneligibleReason(Rental rental)
        {
            if (rental.Status is not (RentalStatus.Returned or RentalStatus.Overdue or RentalStatus.Renewed))
            {
                return "只有已归还 / 逾期 / 已续租的租赁单可以发送结算信息。";
            }

            if (rental.Status == RentalStatus.Renewed)
            {
                return null;
            }

            if (HasPendingInboundShipment(rental)
                || (rental.Status == RentalStatus.Overdue && !HasDeliveredInboundShipment(rental)))
            {
                return "回货物流入库签收后才能发送结算信息。";
            }

            return null;
        }

        private static bool HasDeliveredInboundShipment(Rental rental) =>
            rental.Shipments.Any(s => s.Direction == ShipmentDirection.Inbound && s.DeliveredAt.HasValue);

        private static bool HasPendingInboundShipment(Rental rental) =>
            rental.Shipments.Any(s => s.Direction == ShipmentDirection.Inbound && !s.DeliveredAt.HasValue);

        private static decimal AccountedAmount(Rental rental)
        {
            var shippingFee = rental.Shipments.Sum(s => s.ShippingFee ?? 0m);
            var accountedAmount = rental.TotalPrice - rental.OtherFee - shippingFee;
            return accountedAmount < 0 ? 0 : accountedAmount;
        }

        private static string BuildSettlementMarkdown(
            Rental rental,
            SettlementSetting settings,
            decimal accountedAmount,
            decimal technicianAmount,
            decimal creatorAmount,
            IReadOnlyList<(string? ShipperName, decimal Amount)> shipperShares,
            IReadOnlyList<(string? OwnerName, decimal Amount, string? ItemShortId, string? ItemName)> ownerShares)
        {
            var lines = new List<string>
            {
                $"结算单：{rental.RentalNumber}",
                $"类型：{FormatSettlementType(rental)}",
                $"状态：{FormatStatus(rental.Status)}"
            };

            if (!string.IsNullOrWhiteSpace(rental.RenewedFromRentalNumber))
            {
                lines.Add($"续租自：{rental.RenewedFromRentalNumber.Trim()}");
            }

            if (!string.IsNullOrWhiteSpace(rental.RenewedToRentalNumber))
            {
                lines.Add($"续租到：{rental.RenewedToRentalNumber.Trim()}");
            }

            lines.Add($"日期：{FormatDate(rental.StartDate)} - {FormatDate(rental.ExpectedEndDate)}");
            lines.Add("物品：");
            lines.Add(BuildItemSummary(rental));
            lines.Add($"总价：{FormatMoney(rental.TotalPrice)}");
            lines.Add($"核算：{FormatMoney(accountedAmount)}");
            if (!string.IsNullOrWhiteSpace(rental.PaymentAccount))
            {
                lines.Add($"到账账户：{rental.PaymentAccount.Trim()}");
            }

            if (technicianAmount > 0)
            {
                lines.Add($"技术：{FormatAmount(technicianAmount)}（{FormatPercent(settings.TechnicianPercent)}）");
            }

            if (creatorAmount > 0 && !string.IsNullOrWhiteSpace(rental.CreatedBy))
            {
                lines.Add($"建单（{rental.CreatedBy.Trim()}）：{FormatAmount(creatorAmount)}（{FormatPercent(settings.CreatorPercent)}）");
            }

            foreach (var shipperShare in shipperShares)
            {
                var shipperLabel = string.IsNullOrWhiteSpace(shipperShare.ShipperName)
                    ? string.Empty
                    : $"（{shipperShare.ShipperName}）";
                lines.Add($"发货人{shipperLabel}：{FormatAmount(shipperShare.Amount)}（{FormatPercent(settings.ShipperPercent)}）");
            }

            foreach (var ownerShare in ownerShares)
            {
                var ownerLabel = string.IsNullOrWhiteSpace(ownerShare.OwnerName)
                    ? string.Empty
                    : $"（{ownerShare.OwnerName}）";
                var itemLabel = FormatOwnerShareItem(ownerShare.ItemShortId, ownerShare.ItemName);
                if (!string.IsNullOrWhiteSpace(itemLabel))
                {
                    ownerLabel += $" / {itemLabel}";
                }
                lines.Add($"物品所有{ownerLabel}：{FormatAmount(ownerShare.Amount)}（{FormatPercent(settings.ItemOwnerPercent)}）");
            }

            return $"```text\n{string.Join("\n", lines)}\n```";
        }

        private static string BuildItemSummary(Rental rental)
        {
            var items = rental.Items
                .OrderBy(i => i.Id)
                .Select(i => $"{i.ItemShortIdSnapshot} / {i.ItemNameSnapshot}".Trim())
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToList();

            return items.Count == 0 ? "-" : string.Join("\n", items);
        }

        private static string? FormatOwnerShareItem(string? itemShortId, string? itemName)
        {
            var parts = new[] { itemShortId, itemName }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim())
                .ToList();

            return parts.Count == 0 ? null : string.Join(" / ", parts);
        }

        private static IReadOnlyList<(string? ShipperName, decimal Amount)> BuildShipperShares(
            string? senderNameOverride,
            IReadOnlyList<RentalShipment> sourceShipments,
            decimal accountedAmount,
            decimal shipperPercent)
        {
            var shipperPool = PercentAmount(accountedAmount, shipperPercent);
            if (shipperPool <= 0)
            {
                return Array.Empty<(string? ShipperName, decimal Amount)>();
            }

            if (!string.IsNullOrWhiteSpace(senderNameOverride))
            {
                return [(senderNameOverride.Trim(), shipperPool)];
            }

            var shipments = sourceShipments
                .Where(s => s.Direction == ShipmentDirection.Outbound)
                .ToList();
            if (shipments.Count == 0)
            {
                return [(null, shipperPool)];
            }

            var perShipment = shipperPool / shipments.Count;
            return shipments
                .Select(s => new
                {
                    ShipperName = string.IsNullOrWhiteSpace(s.CreatedBy) ? null : s.CreatedBy.Trim(),
                    Amount = perShipment
                })
                .GroupBy(s => s.ShipperName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .Select(g => (ShipperName: g.Key, Amount: RoundMoney(g.Sum(s => s.Amount))))
                .Where(s => s.Amount > 0)
                .OrderBy(s => string.IsNullOrWhiteSpace(s.ShipperName))
                .ThenByDescending(s => s.Amount)
                .ThenBy(s => s.ShipperName)
                .Select(s => (ShipperName: string.IsNullOrWhiteSpace(s.ShipperName) ? null : s.ShipperName, Amount: s.Amount))
                .ToList();
        }

        private static decimal PercentAmount(decimal amount, decimal percent) =>
            RoundMoney(amount * percent / 100m);

        private static decimal RoundMoney(decimal value) =>
            Math.Round(value, 1, MidpointRounding.AwayFromZero);

        private static string FormatMoney(decimal value) =>
            $"￥{value.ToString("0.0", CultureInfo.InvariantCulture)}";

        private static string FormatAmount(decimal value) =>
            value.ToString("0.0", CultureInfo.InvariantCulture);

        private static string FormatPercent(decimal value) =>
            $"{value.ToString("0.#", CultureInfo.InvariantCulture)}%";

        private static string FormatDate(DateTime value) =>
            RentalDateRules.ToBusinessDate(value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        private static string FormatSettlementType(Rental rental)
        {
            if (rental.Status == RentalStatus.Renewed)
            {
                return "续租结算";
            }

            return string.IsNullOrWhiteSpace(rental.RenewedFromRentalNumber)
                ? "租赁结算"
                : "续租单结算";
        }

        private static string FormatStatus(RentalStatus status) => status switch
        {
            RentalStatus.Pending => "待发货",
            RentalStatus.PartiallyShipped => "未完全发货",
            RentalStatus.Active => "进行中",
            RentalStatus.Overdue => "逾期",
            RentalStatus.Returned => "已归还",
            RentalStatus.Cancelled => "已取消",
            RentalStatus.Renewed => "已续租",
            _ => status.ToString()
        };

        private static string? NormalizeNullableText(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static List<string> NormalizePaymentAccountPresets(IEnumerable<string>? values) =>
            (values ?? Enumerable.Empty<string>())
                .Select(NormalizeNullableText)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(30)
                .ToList();

        private static string? SerializePaymentAccountPresets(IEnumerable<string>? values)
        {
            var presets = NormalizePaymentAccountPresets(values);
            return presets.Count == 0 ? null : JsonSerializer.Serialize(presets);
        }

        private static List<string> DeserializePaymentAccountPresets(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new List<string>();
            }

            try
            {
                return NormalizePaymentAccountPresets(JsonSerializer.Deserialize<List<string>>(json));
            }
            catch (JsonException)
            {
                return new List<string>();
            }
        }

        private static SettlementSettingDto ToDto(SettlementSetting settings) => new()
        {
            TechnicianPercent = settings.TechnicianPercent,
            CreatorPercent = settings.CreatorPercent,
            ShipperPercent = settings.ShipperPercent,
            ItemOwnerPercent = settings.ItemOwnerPercent,
            DefaultPaymentAccount = settings.DefaultPaymentAccount,
            PaymentAccountPresets = DeserializePaymentAccountPresets(settings.PaymentAccountPresetsJson),
            UpdatedAt = settings.UpdatedAt,
            UpdatedBy = settings.UpdatedBy
        };
    }
}
