using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AuditIt.Api.Data;
using AuditIt.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuditIt.Api.Services;

/// <summary>
/// Delivers the approved shipment-reminder template to every applicable internal
/// recipient. A dispatch reservation is written before the provider call so repeat
/// sweeps and multiple application instances do not send duplicates.
/// </summary>
public class ShipmentReminderService : IShipmentReminderService
{
    private static readonly JsonSerializerOptions TemplateVariablesJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ApplicationDbContext _db;
    private readonly IAliyunShipmentReminderSender _sender;
    private readonly AliyunNotificationOptions _aliyunOptions;
    private readonly ILogger<ShipmentReminderService> _logger;

    public ShipmentReminderService(
        ApplicationDbContext db,
        IAliyunShipmentReminderSender sender,
        IOptions<AliyunNotificationOptions> aliyunOptions,
        ILogger<ShipmentReminderService> logger)
    {
        _db = db;
        _sender = sender;
        _aliyunOptions = aliyunOptions.Value;
        _logger = logger;
    }

    public async Task<ShipmentReminderSettingsDto> GetSettingsAsync(CancellationToken ct = default)
    {
        var settings = await GetOrCreateSettingsAsync(ct);
        return ToDto(settings);
    }

    public async Task<IReadOnlyList<ShipmentReminderRecipientDto>> ListActiveRecipientsAsync(CancellationToken ct = default) =>
        await _db.Users
            .AsNoTracking()
            .Where(user => user.Status == UserStatus.Active)
            .OrderBy(user => user.Name)
            .Select(user => new ShipmentReminderRecipientDto
            {
                Id = user.Id,
                Name = user.Name,
                Mobile = user.Mobile
            })
            .ToListAsync(ct);

    public async Task<ShipmentReminderSettingsDto> UpdateSettingsAsync(
        UpdateShipmentReminderSettingsDto dto,
        string? currentUser,
        CancellationToken ct = default)
    {
        ValidateChannelConfiguration(dto);

        AliyunSmsTemplateDto? smsTemplate = null;
        if (dto.SmsEnabled)
        {
            if (!_aliyunOptions.IsConfigured)
            {
                throw new InvalidOperationException("请先在服务端配置阿里云 AccessKey，再从阿里云加载短信模板。");
            }

            smsTemplate = (await ListSmsTemplatesAsync(ct)).FirstOrDefault(template =>
                template.TemplateCode == dto.SmsTemplateCode
                && template.IsApproved
                && template.MatchesShipmentReminder);
            if (smsTemplate == null)
            {
                throw new InvalidOperationException("请选择阿里云中已审核通过且内容、变量完全匹配的发货提醒短信模板。");
            }

            ValidateTemplateVariables(dto.TemplateVariables, smsTemplate.VariableNames);
        }

        var administratorIds = dto.AdministratorUserIds.Distinct().ToList();
        if (administratorIds.Count > 0)
        {
            var activeUserCount = await _db.Users.CountAsync(
                user => user.Status == UserStatus.Active && administratorIds.Contains(user.Id), ct);
            if (activeUserCount != administratorIds.Count)
            {
                throw new InvalidOperationException("指定管理员中包含不存在或已离职的用户。");
            }
        }

        var settings = await GetOrCreateSettingsAsync(ct);
        settings.Enabled = dto.Enabled;
        settings.SmsEnabled = dto.SmsEnabled;
        settings.VoiceEnabled = dto.VoiceEnabled;
        settings.SendHour = dto.SendHour;
        settings.SendMinute = dto.SendMinute;
        settings.VoiceSendHour = dto.VoiceSendHour;
        settings.VoiceSendMinute = dto.VoiceSendMinute;
        settings.TemplateVariablesJson = JsonSerializer.Serialize(NormalizeTemplateVariables(dto.TemplateVariables), TemplateVariablesJsonOptions);
        settings.SmsSignName = smsTemplate?.SignatureName;
        settings.SmsTemplateCode = Normalize(dto.SmsTemplateCode);
        settings.VoiceTtsCode = Normalize(dto.VoiceTtsCode);
        settings.VoiceCalledShowNumber = Normalize(dto.VoiceCalledShowNumber);
        settings.AdministratorUserIds = JsonSerializer.Serialize(administratorIds);
        settings.UpdatedAt = DateTime.UtcNow;
        settings.UpdatedBy = Normalize(currentUser);

        await _db.SaveChangesAsync(ct);
        return ToDto(settings);
    }

    public async Task<IReadOnlyList<AliyunSmsTemplateDto>> ListSmsTemplatesAsync(CancellationToken ct = default)
    {
        if (!_aliyunOptions.IsConfigured)
        {
            throw new InvalidOperationException("阿里云 AccessKey 未配置，无法加载短信模板。");
        }

        return await _sender.ListSmsTemplatesAsync(ct);
    }

    public async Task<IReadOnlyList<ShipmentReminderTestResultDto>> SendTestAsync(
        TestShipmentReminderDto dto,
        CancellationToken ct = default)
    {
        if (!dto.SendSms && !dto.SendVoice)
        {
            throw new InvalidOperationException("请至少选择一种测试发送方式。");
        }

        var settings = await GetOrCreateSettingsAsync(ct);
        ValidateTestChannelConfiguration(settings, dto);

        var requestedIds = dto.UserIds.Distinct().ToList();
        var users = await _db.Users
            .Where(user => user.Status == UserStatus.Active && requestedIds.Contains(user.Id))
            .OrderBy(user => user.Name)
            .ToListAsync(ct);
        if (users.Count != requestedIds.Count)
        {
            throw new InvalidOperationException("测试对象中包含不存在或已离职的用户。");
        }

        var recipients = users
            .Select(user => new Recipient(user.Name, NormalizeMobile(user.Mobile)))
            .Where(recipient => recipient.Mobile != null)
            .GroupBy(recipient => recipient.Mobile!, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        if (recipients.Count == 0)
        {
            throw new InvalidOperationException("所选用户均未配置有效的中国大陆手机号。");
        }

        var results = new List<ShipmentReminderTestResultDto>();
        var templateParameters = BuildTestTemplateParameters(settings);
        foreach (var recipient in recipients)
        {
            if (dto.SendSms)
            {
                results.Add(await SendTestChannelAsync(
                    recipient,
                    ShipmentReminderChannel.Sms,
                    settings,
                    templateParameters,
                    ct));
            }

            if (dto.SendVoice)
            {
                results.Add(await SendTestChannelAsync(
                    recipient,
                    ShipmentReminderChannel.Voice,
                    settings,
                    templateParameters,
                    ct));
            }
        }

        return results;
    }

    public async Task DispatchScheduledAsync(DateTime utcNow, CancellationToken ct = default)
    {
        var settings = await GetOrCreateSettingsAsync(ct);
        var channels = GetDueChannels(settings, utcNow).ToList();
        if (!settings.Enabled || channels.Count == 0)
        {
            return;
        }

        if (!_aliyunOptions.IsConfigured)
        {
            _logger.LogWarning("Shipment reminder is enabled but Alibaba Cloud credentials are not configured.");
            return;
        }

        var businessDate = RentalDateRules.Today(utcNow);
        var rentals = await _db.Rentals
            .Include(rental => rental.Renter)
            .Include(rental => rental.Shipments)
            .Where(rental => rental.Status == RentalStatus.Pending)
            .ToListAsync(ct);
        var candidates = rentals
            .Where(rental => RentalDateRules.ToBusinessDate(rental.ExpectedShipDate) <= businessDate
                && !rental.RenewedFromRentalId.HasValue
                && !rental.Shipments.Any(shipment => shipment.Direction == ShipmentDirection.Outbound))
            .ToList();
        if (candidates.Count == 0)
        {
            return;
        }

        var administrators = ParseAdministratorIds(settings.AdministratorUserIds);
        foreach (var rental in candidates)
        {
            var recipients = await ResolveRecipientsAsync(rental, administrators, ct);
            var templateParameters = BuildTemplateParameters(rental, settings, businessDate);

            foreach (var recipient in recipients)
            {
                foreach (var channel in channels)
                {
                    var dispatch = await ReserveDispatchAsync(rental.Id, recipient, channel, businessDate, utcNow, ct);
                    if (dispatch == null)
                    {
                        continue;
                    }

                    try
                    {
                        dispatch.ProviderRequestId = await SendChannelAsync(
                            channel, recipient.Mobile!, settings, templateParameters, ct);
                        dispatch.Status = ShipmentReminderDispatchStatus.Succeeded;
                        dispatch.SentAt = DateTime.UtcNow;
                        dispatch.ErrorMessage = null;
                        await _db.SaveChangesAsync(ct);
                    }
                    catch (Exception ex)
                    {
                        dispatch.Status = ShipmentReminderDispatchStatus.Failed;
                        dispatch.ErrorMessage = Truncate(ex.Message, 1000);
                        await _db.SaveChangesAsync(ct);
                        _logger.LogWarning(ex,
                            "Shipment {Channel} reminder failed for rental {RentalNumber}, recipient {RecipientName}.",
                            channel,
                            rental.RentalNumber,
                            recipient.Name);
                    }
                }
            }
        }
    }

    private async Task<ShipmentReminderTestResultDto> SendTestChannelAsync(
        Recipient recipient,
        ShipmentReminderChannel channel,
        ShipmentReminderSettings settings,
        IReadOnlyDictionary<string, string> templateParameters,
        CancellationToken ct)
    {
        try
        {
            var requestId = await SendChannelAsync(channel, recipient.Mobile!, settings, templateParameters, ct);
            return new ShipmentReminderTestResultDto
            {
                UserName = recipient.Name,
                Mobile = recipient.Mobile!,
                Channel = channel,
                Success = true,
                ProviderRequestId = requestId
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Shipment reminder test failed for {RecipientName} through {Channel}.", recipient.Name, channel);
            return new ShipmentReminderTestResultDto
            {
                UserName = recipient.Name,
                Mobile = recipient.Mobile!,
                Channel = channel,
                Success = false,
                Error = ex.Message
            };
        }
    }

    private Task<string> SendChannelAsync(
        ShipmentReminderChannel channel,
        string mobile,
        ShipmentReminderSettings settings,
        IReadOnlyDictionary<string, string> templateParameters,
        CancellationToken ct) => channel switch
    {
        ShipmentReminderChannel.Sms => _sender.SendSmsAsync(
            mobile,
            settings.SmsSignName!,
            settings.SmsTemplateCode!,
            templateParameters,
            ct),
        ShipmentReminderChannel.Voice => _sender.SendVoiceAsync(
            mobile,
            settings.VoiceCalledShowNumber,
            settings.VoiceTtsCode!,
            templateParameters,
            ct),
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, null)
    };

    private async Task<List<Recipient>> ResolveRecipientsAsync(
        Rental rental,
        IReadOnlyCollection<Guid> administratorIds,
        CancellationToken ct)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddName(names, rental.CreatedBy);
        foreach (var assignedTo in SplitNames(rental.AssignedTo))
        {
            AddName(names, assignedTo);
        }

        var users = await _db.Users
            .Where(user => user.Status == UserStatus.Active
                && (names.Contains(user.Name) || administratorIds.Contains(user.Id)))
            .OrderBy(user => user.Name)
            .ToListAsync(ct);

        return users
            .Select(user => new Recipient(user.Name, NormalizeMobile(user.Mobile)))
            .Where(recipient => recipient.Mobile != null)
            .GroupBy(recipient => recipient.Mobile!, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
    }

    private async Task<ShipmentReminderDispatch?> ReserveDispatchAsync(
        Guid rentalId,
        Recipient recipient,
        ShipmentReminderChannel channel,
        DateTime businessDate,
        DateTime utcNow,
        CancellationToken ct)
    {
        var dispatch = await _db.ShipmentReminderDispatches.FirstOrDefaultAsync(item =>
            item.RentalId == rentalId
            && item.RecipientMobile == recipient.Mobile
            && item.Channel == channel
            && item.BusinessDate == businessDate, ct);

        if (dispatch?.Status is ShipmentReminderDispatchStatus.Succeeded or ShipmentReminderDispatchStatus.Pending)
        {
            return null;
        }

        if (dispatch == null)
        {
            dispatch = new ShipmentReminderDispatch
            {
                RentalId = rentalId,
                RecipientMobile = recipient.Mobile!,
                RecipientName = recipient.Name,
                Channel = channel,
                BusinessDate = businessDate,
                AttemptedAt = utcNow,
                Status = ShipmentReminderDispatchStatus.Pending
            };
            _db.ShipmentReminderDispatches.Add(dispatch);
        }
        else
        {
            dispatch.RecipientName = recipient.Name;
            dispatch.AttemptedAt = utcNow;
            dispatch.ErrorMessage = null;
            dispatch.Status = ShipmentReminderDispatchStatus.Pending;
        }

        try
        {
            await _db.SaveChangesAsync(ct);
            return dispatch;
        }
        catch (DbUpdateException ex)
        {
            _logger.LogDebug(ex, "Shipment reminder dispatch was already reserved by another worker.");
            _db.Entry(dispatch).State = EntityState.Detached;
            return null;
        }
    }

    private async Task<ShipmentReminderSettings> GetOrCreateSettingsAsync(CancellationToken ct)
    {
        var settings = await _db.ShipmentReminderSettings.FindAsync([ShipmentReminderSettings.SingletonId], ct);
        if (settings != null)
        {
            return settings;
        }

        settings = new ShipmentReminderSettings();
        _db.ShipmentReminderSettings.Add(settings);
        await _db.SaveChangesAsync(ct);
        return settings;
    }

    private ShipmentReminderSettingsDto ToDto(ShipmentReminderSettings settings) => new()
    {
        Enabled = settings.Enabled,
        SmsEnabled = settings.SmsEnabled,
        VoiceEnabled = settings.VoiceEnabled,
        SendHour = settings.SendHour,
        SendMinute = settings.SendMinute,
        VoiceSendHour = settings.VoiceSendHour,
        VoiceSendMinute = settings.VoiceSendMinute,
        TemplateVariables = ParseTemplateVariables(settings.TemplateVariablesJson),
        SmsSignName = settings.SmsSignName,
        SmsTemplateCode = settings.SmsTemplateCode,
        VoiceTtsCode = settings.VoiceTtsCode,
        VoiceCalledShowNumber = settings.VoiceCalledShowNumber,
        AdministratorUserIds = ParseAdministratorIds(settings.AdministratorUserIds).ToList(),
        TemplateBody = ShipmentReminderTemplate.Body,
        AliyunCredentialsConfigured = _aliyunOptions.IsConfigured,
        UpdatedAt = settings.UpdatedAt,
        UpdatedBy = settings.UpdatedBy
    };

    private static bool IsDue(ShipmentReminderSettings settings, ShipmentReminderChannel channel, DateTime utcNow)
    {
        var chinaNow = utcNow.AddHours(8);
        var scheduledTime = channel == ShipmentReminderChannel.Voice
            ? new TimeSpan(settings.VoiceSendHour, settings.VoiceSendMinute, 0)
            : new TimeSpan(settings.SendHour, settings.SendMinute, 0);

        return chinaNow.TimeOfDay >= scheduledTime;
    }

    private static IEnumerable<ShipmentReminderChannel> GetDueChannels(ShipmentReminderSettings settings, DateTime utcNow)
    {
        if (settings.SmsEnabled && IsDue(settings, ShipmentReminderChannel.Sms, utcNow))
        {
            yield return ShipmentReminderChannel.Sms;
        }

        if (settings.VoiceEnabled && IsDue(settings, ShipmentReminderChannel.Voice, utcNow))
        {
            yield return ShipmentReminderChannel.Voice;
        }
    }

    private static void ValidateChannelConfiguration(UpdateShipmentReminderSettingsDto dto)
    {
        ValidateTemplateVariables(dto.TemplateVariables, null);

        if (dto.Enabled && !dto.SmsEnabled && !dto.VoiceEnabled)
        {
            throw new InvalidOperationException("启用发货提醒时，至少应开启短信或语音中的一种方式。");
        }

        if (dto.SmsEnabled && string.IsNullOrWhiteSpace(dto.SmsTemplateCode))
        {
            throw new InvalidOperationException("启用短信时必须从阿里云选择模板。");
        }

        if (dto.VoiceEnabled && string.IsNullOrWhiteSpace(dto.VoiceTtsCode))
        {
            throw new InvalidOperationException("启用语音时必须填写语音 TTS Code。");
        }

    }

    private static void ValidateTestChannelConfiguration(ShipmentReminderSettings settings, TestShipmentReminderDto dto)
    {
        if (dto.SendSms && (string.IsNullOrWhiteSpace(settings.SmsSignName) || string.IsNullOrWhiteSpace(settings.SmsTemplateCode)))
        {
            throw new InvalidOperationException("请先配置短信签名和模板 Code。");
        }

        if (dto.SendVoice && string.IsNullOrWhiteSpace(settings.VoiceTtsCode))
        {
            throw new InvalidOperationException("请先配置语音 TTS Code。");
        }
    }

    private static void ValidateTemplateVariables(
        IReadOnlyCollection<ShipmentReminderTemplateVariableDto>? variables,
        IReadOnlyCollection<string>? expectedVariableNames)
    {
        if (variables == null || variables.Count == 0)
        {
            throw new InvalidOperationException("请至少配置一个模板变量。");
        }

        var normalized = NormalizeTemplateVariables(variables);
        if (normalized.Count != variables.Count || normalized.Select(variable => variable.Name).Distinct(StringComparer.Ordinal).Count() != normalized.Count)
        {
            throw new InvalidOperationException("模板变量名不能为空，必须以英文字母开头且不可重复。");
        }

        if (expectedVariableNames != null
            && !normalized.Select(variable => variable.Name).ToHashSet(StringComparer.Ordinal)
                .SetEquals(expectedVariableNames))
        {
            throw new InvalidOperationException("变量配置必须与所选阿里云短信模板中的变量完全一致。");
        }
    }

    private static bool IsVariableName(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Regex.IsMatch(value, "^[A-Za-z][A-Za-z0-9_]*$");

    private static List<ShipmentReminderTemplateVariableDto> NormalizeTemplateVariables(
        IEnumerable<ShipmentReminderTemplateVariableDto> variables) => variables
        .Select(variable => new ShipmentReminderTemplateVariableDto
        {
            Name = variable.Name?.Trim() ?? string.Empty,
            Source = variable.Source,
            StaticValue = Normalize(variable.StaticValue)
        })
        .Where(variable => IsVariableName(variable.Name))
        .ToList();

    private static List<ShipmentReminderTemplateVariableDto> ParseTemplateVariables(string? value)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<List<ShipmentReminderTemplateVariableDto>>(
                value ?? ShipmentReminderTemplate.DefaultVariablesJson,
                TemplateVariablesJsonOptions);
            var normalized = NormalizeTemplateVariables(parsed ?? []);
            return normalized.Count > 0 ? normalized : DefaultTemplateVariables();
        }
        catch (JsonException)
        {
            return DefaultTemplateVariables();
        }
    }

    private static List<ShipmentReminderTemplateVariableDto> DefaultTemplateVariables() =>
    [
        new()
        {
            Name = ShipmentReminderTemplate.DefaultOrderIdVariableName,
            Source = ShipmentReminderVariableSource.OrderIdWithRenterName
        },
        new()
        {
            Name = ShipmentReminderTemplate.DefaultTimeVariableName,
            Source = ShipmentReminderVariableSource.RelativeExpectedShipDate
        }
    ];

    private static IReadOnlyDictionary<string, string> BuildTemplateParameters(
        Rental rental,
        ShipmentReminderSettings settings,
        DateTime businessDate)
    {
        var expectedShipDate = RentalDateRules.ToBusinessDate(rental.ExpectedShipDate);
        return ParseTemplateVariables(settings.TemplateVariablesJson)
            .ToDictionary(
                variable => variable.Name,
                variable => ResolveVariableValue(
                    variable,
                    BuildOrderId(rental),
                    rental.RentalNumber,
                    rental.Renter?.Name ?? string.Empty,
                    RelativeDayText(expectedShipDate, businessDate),
                    expectedShipDate.ToString("yyyy年M月d日"),
                    rental.CreatedBy ?? string.Empty,
                    rental.AssignedTo ?? string.Empty),
                StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, string> BuildTestTemplateParameters(ShipmentReminderSettings settings) =>
        ParseTemplateVariables(settings.TemplateVariablesJson)
            .ToDictionary(
                variable => variable.Name,
                variable => ResolveVariableValue(
                    variable,
                    "TEST-ORDER 测试租客",
                    "TEST-ORDER",
                    "测试租客",
                    "今天",
                    "2026年8月8日",
                    "测试建单人",
                    "测试负责人"),
                StringComparer.Ordinal);

    private static string ResolveVariableValue(
        ShipmentReminderTemplateVariableDto variable,
        string orderIdWithRenterName,
        string rentalNumber,
        string renterName,
        string relativeExpectedShipDate,
        string expectedShipDate,
        string creator,
        string responsible) => variable.Source switch
    {
        ShipmentReminderVariableSource.OrderIdWithRenterName => orderIdWithRenterName,
        ShipmentReminderVariableSource.RentalNumber => rentalNumber,
        ShipmentReminderVariableSource.RenterName => renterName,
        ShipmentReminderVariableSource.RelativeExpectedShipDate => relativeExpectedShipDate,
        ShipmentReminderVariableSource.ExpectedShipDate => expectedShipDate,
        ShipmentReminderVariableSource.Creator => creator,
        ShipmentReminderVariableSource.Responsible => responsible,
        ShipmentReminderVariableSource.StaticText => variable.StaticValue ?? string.Empty,
        _ => string.Empty
    };

    private static IReadOnlyCollection<Guid> ParseAdministratorIds(string? value)
    {
        try
        {
            return JsonSerializer.Deserialize<List<Guid>>(value ?? "[]")?
                .Distinct()
                .ToArray() ?? Array.Empty<Guid>();
        }
        catch (JsonException)
        {
            return Array.Empty<Guid>();
        }
    }

    private static string BuildOrderId(Rental rental)
    {
        var renterName = rental.Renter?.Name?.Trim();
        return string.IsNullOrWhiteSpace(renterName)
            ? rental.RentalNumber
            : $"{rental.RentalNumber} {renterName}";
    }

    private static string RelativeDayText(DateTime date, DateTime today)
    {
        var difference = (date.Date - today.Date).Days;
        return difference switch
        {
            -1 => "昨天",
            0 => "今天",
            1 => "明天",
            < -1 => $"{-difference}天前",
            _ => $"{date:yyyy年M月d日}"
        };
    }

    private static IEnumerable<string> SplitNames(string? names) =>
        string.IsNullOrWhiteSpace(names)
            ? Array.Empty<string>()
            : names.Split([',', ';', '，', '；', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void AddName(ISet<string> names, string? name)
    {
        var normalized = Normalize(name);
        if (!string.IsNullOrEmpty(normalized))
        {
            names.Add(normalized);
        }
    }

    private static string? NormalizeMobile(string? mobile)
    {
        var digits = new string((mobile ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.Length == 13 && digits.StartsWith("86", StringComparison.Ordinal))
        {
            digits = digits[2..];
        }

        return digits.Length == 11 && digits.StartsWith('1') ? digits : null;
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private sealed record Recipient(string Name, string? Mobile);
}
