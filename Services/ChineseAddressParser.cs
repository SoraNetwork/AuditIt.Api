using System.Text.RegularExpressions;
using AuditIt.Api.Models;

namespace AuditIt.Api.Services
{
    public static class ChineseAddressParser
    {
        private static readonly string[] ProvinceNames =
        {
            "新疆维吾尔自治区", "广西壮族自治区", "宁夏回族自治区", "内蒙古自治区", "西藏自治区",
            "黑龙江省", "吉林省", "辽宁省", "河北省", "山西省", "江苏省", "浙江省", "安徽省",
            "福建省", "江西省", "山东省", "河南省", "湖北省", "湖南省", "广东省", "海南省",
            "四川省", "贵州省", "云南省", "陕西省", "甘肃省", "青海省", "台湾省",
            "北京市", "天津市", "上海市", "重庆市", "香港特别行政区", "澳门特别行政区",
        };

        public static SfParsedAddressDto Parse(string? address)
        {
            var value = RemoveWhitespace(address);
            var provinceMatch = ProvinceNames
                .Select(name => new { Name = name, Index = value.IndexOf(name, StringComparison.Ordinal) })
                .Where(candidate => candidate.Index >= 0)
                .OrderBy(candidate => candidate.Index)
                .ThenByDescending(candidate => candidate.Name.Length)
                .FirstOrDefault();
            var province = provinceMatch?.Name;

            string? normalizedProvince = province;
            if (province is "北京市" or "天津市" or "上海市" or "重庆市")
            {
                normalizedProvince = province[..2];
            }

            var remaining = provinceMatch == null
                ? value
                : value[(provinceMatch.Index + provinceMatch.Name.Length)..];
            string? city = normalizedProvince;
            if (normalizedProvince is not ("北京" or "天津" or "上海" or "重庆")
                && remaining.Length > 0)
            {
                var cityMatch = Regex.Match(
                    remaining,
                    "^(?<city>[\\u4e00-\\u9fff]{2,12}(?:市|自治州|地区|盟))");
                city = cityMatch.Success ? cityMatch.Groups["city"].Value : null;
                if (city != null) remaining = remaining[city.Length..];
            }

            var districtMatch = Regex.Match(
                remaining,
                "^(?<district>[\\u4e00-\\u9fff]{2,12}(?:区|县|旗|市))");

            return new SfParsedAddressDto
            {
                Province = normalizedProvince,
                City = city,
                District = districtMatch.Success ? districtMatch.Groups["district"].Value : null,
            };
        }

        public static string NormalizeForSf(string? address)
        {
            var value = RemoveWhitespace(address).Trim();
            if (value.Length == 0) return value;

            var provinceIndex = ProvinceNames
                .Select(name => value.IndexOf(name, StringComparison.Ordinal))
                .Where(index => index >= 0)
                .OrderBy(index => index)
                .FirstOrDefault(-1);
            return provinceIndex >= 0 ? value[provinceIndex..] : value;
        }

        private static string RemoveWhitespace(string? value) =>
            (value ?? string.Empty).Replace(" ", string.Empty).Replace("　", string.Empty);
    }
}
