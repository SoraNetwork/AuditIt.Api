using System.Text.Json;
using AuditIt.Api.Models;
using Microsoft.Extensions.Options;
using OpenApiConfig = AlibabaCloud.OpenApiClient.Models.Config;
using DysmsClient = AlibabaCloud.SDK.Dysmsapi20170525.Client;
using DysmsModels = AlibabaCloud.SDK.Dysmsapi20170525.Models;
using DyvmsClient = AlibabaCloud.SDK.Dyvmsapi20170525.Client;
using DyvmsModels = AlibabaCloud.SDK.Dyvmsapi20170525.Models;

namespace AuditIt.Api.Services;

/// <summary>Thin adapter around the official Alibaba Cloud DYSMS and DYVMS SDKs.</summary>
public class AliyunShipmentReminderSender : IAliyunShipmentReminderSender
{
    private readonly AliyunNotificationOptions _options;

    public AliyunShipmentReminderSender(IOptions<AliyunNotificationOptions> options)
    {
        _options = options.Value;
    }

    public async Task<string> SendSmsAsync(
        string mobile,
        string signName,
        string templateCode,
        IReadOnlyDictionary<string, string> templateParameters,
        CancellationToken ct)
    {
        var response = await CreateSmsClient().SendSmsAsync(new DysmsModels.SendSmsRequest
        {
            PhoneNumbers = mobile,
            SignName = signName,
            TemplateCode = templateCode,
            TemplateParam = BuildTemplateParameters(templateParameters)
        });

        EnsureSucceeded(response.Body?.Code, response.Body?.Message, "短信");
        return response.Body?.RequestId ?? string.Empty;
    }

    public async Task<IReadOnlyList<AliyunSmsTemplateDto>> ListSmsTemplatesAsync(CancellationToken ct)
    {
        var templates = new List<AliyunSmsTemplateDto>();
        var pageIndex = 1;
        const int pageSize = 50;

        while (!ct.IsCancellationRequested)
        {
            var response = await CreateSmsClient().QuerySmsTemplateListAsync(new DysmsModels.QuerySmsTemplateListRequest
            {
                PageIndex = pageIndex,
                PageSize = pageSize
            });
            EnsureSucceeded(response.Body?.Code, response.Body?.Message, "短信模板查询");

            var page = response.Body?.SmsTemplateList ?? [];
            templates.AddRange(page.Select(template => new AliyunSmsTemplateDto
            {
                TemplateCode = template.TemplateCode ?? string.Empty,
                TemplateName = template.TemplateName,
                TemplateContent = template.TemplateContent,
                SignatureName = template.SignatureName,
                AuditStatus = template.AuditStatus,
                IsApproved = string.Equals(template.AuditStatus, "AUDIT_STATE_PASS", StringComparison.OrdinalIgnoreCase),
                MatchesShipmentReminder = MatchesShipmentTemplate(template.TemplateContent),
                VariableNames = ExtractVariableNames(template.TemplateContent)
            }));

            var totalCount = response.Body?.TotalCount ?? 0;
            if (page.Count < pageSize || templates.Count >= totalCount)
            {
                break;
            }

            pageIndex++;
        }

        return templates
            .OrderByDescending(template => template.IsApproved && template.MatchesShipmentReminder)
            .ThenBy(template => template.TemplateName)
            .ToList();
    }

    public async Task<string> SendVoiceAsync(
        string mobile,
        string? calledShowNumber,
        string ttsCode,
        IReadOnlyDictionary<string, string> templateParameters,
        CancellationToken ct)
    {
        var response = await CreateVoiceClient().SingleCallByTtsAsync(new DyvmsModels.SingleCallByTtsRequest
        {
            CalledNumber = mobile,
            CalledShowNumber = calledShowNumber,
            TtsCode = ttsCode,
            TtsParam = BuildTemplateParameters(templateParameters)
        });

        EnsureSucceeded(response.Body?.Code, response.Body?.Message, "语音");
        return response.Body?.RequestId ?? string.Empty;
    }

    private DysmsClient CreateSmsClient() => new(CreateConfig("dysmsapi.aliyuncs.com"));

    private DyvmsClient CreateVoiceClient() => new(CreateConfig("dyvmsapi.aliyuncs.com"));

    private OpenApiConfig CreateConfig(string endpoint)
    {
        if (!_options.IsConfigured)
        {
            throw new InvalidOperationException("阿里云短信/语音 AccessKey 未配置。请在服务端 AliyunNotification 配置节中设置 AccessKeyId 和 AccessKeySecret。");
        }

        return new OpenApiConfig
        {
            AccessKeyId = _options.AccessKeyId,
            AccessKeySecret = _options.AccessKeySecret,
            Endpoint = endpoint
        };
    }

    private static string BuildTemplateParameters(IReadOnlyDictionary<string, string> values) => JsonSerializer.Serialize(values);

    private static void EnsureSucceeded(string? code, string? message, string channel)
    {
        if (!string.Equals(code, "OK", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"阿里云{channel}发送失败：{code ?? "Unknown"} {message ?? string.Empty}".Trim());
        }
    }

    private static bool MatchesShipmentTemplate(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var variableNames = ExtractVariableNames(content);
        if (variableNames.Count != 2 || variableNames.Distinct(StringComparer.Ordinal).Count() != 2)
        {
            return false;
        }

        var normalized = NormalizeTemplateSkeleton(content);
        var expected = NormalizeTemplateSkeleton(ShipmentReminderTemplate.Body);
        return string.Equals(normalized, expected, StringComparison.Ordinal);
    }

    private static List<string> ExtractVariableNames(string? content) =>
        System.Text.RegularExpressions.Regex.Matches(content ?? string.Empty, @"\$\{([A-Za-z][A-Za-z0-9_]*)\}")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static string NormalizeTemplateSkeleton(string content) =>
        System.Text.RegularExpressions.Regex.Replace(content, @"\$\{[A-Za-z][A-Za-z0-9_]*\}", "${variable}")
            .Where(character => !char.IsWhiteSpace(character))
            .Aggregate(string.Empty, (current, character) => current + character);
}
