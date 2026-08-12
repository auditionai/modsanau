using System.Xml.Linq;
using AuditionModStudio.Gateway.Endpoints;

namespace Gateway.Tests;

public sealed class GatewayArchitectureTests
{
    [Fact]
    public void Gateway_is_a_separate_server_project_that_only_references_core()
    {
        var root = FindRepositoryRoot();
        var gateway = XDocument.Load(Path.Combine(root, "src", "AuditionModStudio.Gateway",
            "AuditionModStudio.Gateway.csproj"));
        var app = XDocument.Load(Path.Combine(root, "src", "AuditionModStudio.App",
            "AuditionModStudio.App.csproj"));

        var gatewayReferences = gateway.Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")!.Value.Replace('\\', '/'))
            .ToArray();
        var appReferences = app.Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")!.Value)
            .ToArray();

        Assert.Equal(["../AuditionModStudio.Core/AuditionModStudio.Core.csproj"], gatewayReferences);
        Assert.DoesNotContain(appReferences,
            reference => reference.Contains("AuditionModStudio.Gateway", StringComparison.Ordinal));
    }

    [Fact]
    public void Client_request_models_cannot_supply_server_authority_fields()
    {
        var forbidden = new[]
        {
            "ProviderKey",
            "ServiceRoleKey",
            "UserId",
            "Balance",
            "Cost",
            "Refund",
            "PaymentSucceeded",
            "Granted",
        };
        var requestProperties = new[] { typeof(AiGatewayRequest), typeof(TemplateEntitlementRequest) }
            .SelectMany(type => type.GetProperties())
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain(requestProperties,
            property => forbidden.Contains(property, StringComparer.OrdinalIgnoreCase));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AuditionModStudio.sln"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the solution root.");
    }
}
