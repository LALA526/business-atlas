using System.Text.Json;
using ProjectIdentity.Application;
using SchemaCatalog.Application;
using SchemaCatalog.Contracts;

namespace Atlas.Domain.Tests;

public sealed class RegistryValidatorTests
{
    [Fact]
    public void BuiltInsExposeAllFiveDimensionsAndSemanticPredicates()
    {
        Assert.Equal(["project", "business", "system", "knowledge", "governance"], BuiltInRegistry.Dimensions.Select(item => item.Key));
        Assert.Contains(BuiltInRegistry.Types, item => item.Kind == "TypeDefinition");
        Assert.Contains(BuiltInRegistry.Types, item => item.Kind == "ContentProfile");
        Assert.Contains(BuiltInRegistry.Predicates, item => item.Key == "mapsTo" && item.ImpactDirections.Contains("reverse"));
    }

    [Fact]
    public void TypeProposalRequiresKnownDimensionAndObjectSchema()
    {
        var revision = Revision("TypeDefinition", new { typeKey = "WarrantyPackage", version = "1.0.0", profile = "standard", schema = new { type = "object" }, category = "business" });
        var definition = RegistryValidator.ValidateType(revision);
        Assert.Equal("WarrantyPackage", definition.Key);

        var invalid = Revision("TypeDefinition", new { typeKey = "WarrantyPackage", version = "1.0.0", profile = "standard", schema = new { type = "array" }, category = "business" });
        var error = Assert.Throws<ProjectIdentityException>(() => RegistryValidator.ValidateType(invalid));
        Assert.Equal("VALIDATION_FAILED", error.Code);
    }

    [Fact]
    public void IncompatibleSchemaExplainsRemovedProperty()
    {
        var previous = Definition("1.0.0", new { type = "object", properties = new { code = new { type = "string" } } });
        var next = Definition("1.1.0", new { type = "object", properties = new { } });
        var error = Assert.Throws<ProjectIdentityException>(() => RegistryValidator.EnsureTypeCompatible(next, previous));
        Assert.Equal("CONFLICT", error.Code);
        Assert.Contains("code", error.Message);
        Assert.Contains("cannot be removed", error.Message);
    }

    [Fact]
    public void AdditiveDefaultedRequiredPropertyIsCompatible()
    {
        var previous = Definition("1.0.0", new { type = "object", properties = new { code = new { type = "string" } } });
        var next = Definition("1.1.0", new { type = "object", properties = new { code = new { type = "string" }, active = new { type = "boolean", @default = true } }, required = new[] { "active" } });
        RegistryValidator.EnsureTypeCompatible(next, previous);
    }

    [Fact]
    public void SynonymPredicateWithSameMeaningAndEndpointsIsRejected()
    {
        var existing = new PredicateDefinition("mapsTo", ["BusinessAttribute"], ["DataField"], "业务到实现语义映射", ["forward", "reverse"]);
        var duplicate = new PredicateDefinition("semanticMapping", ["BusinessAttribute"], ["DataField"], "业务到实现：语义映射", ["forward"]);
        var error = Assert.Throws<ProjectIdentityException>(() => RegistryValidator.EnsurePredicateUnique(duplicate, [existing]));
        Assert.Equal("CONFLICT", error.Code);
        Assert.Contains("mapsTo", error.Message);
    }

    private static TypeDefinition Definition(string version, object schema) => new("WarrantyPackage", version, "standard", "business", Element(schema), null);
    private static RevisionInput Revision(string kind, object body) => new(null, "Test registry definition", kind, "1.0.0", "normative", "unverified",
        Element(new { }), Element(body), Element(Array.Empty<object>()), Element(Array.Empty<object>()), Element(Array.Empty<object>()), Element(new[] { kind }));
    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToDocument(value).RootElement.Clone();
}
