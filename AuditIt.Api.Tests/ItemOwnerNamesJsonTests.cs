using AuditIt.Api.Services;
using Xunit;

namespace AuditIt.Api.Tests;

public class ItemOwnerNamesJsonTests
{
    [Fact]
    public void Parse_readsOwnerNamesFromJsonArray()
    {
        var (ownerNames, error) = ItemOwnerNamesJson.Parse("""[" Alice ","Bob","Alice"]""");

        Assert.Null(error);
        Assert.Equal(["Alice", "Bob"], ownerNames);
    }

    [Fact]
    public void Parse_returnsErrorForInvalidJson()
    {
        var (ownerNames, error) = ItemOwnerNamesJson.Parse("""{"name":"Alice"}""");

        Assert.Null(ownerNames);
        Assert.Equal("Owner user names json is invalid.", error);
    }
}
