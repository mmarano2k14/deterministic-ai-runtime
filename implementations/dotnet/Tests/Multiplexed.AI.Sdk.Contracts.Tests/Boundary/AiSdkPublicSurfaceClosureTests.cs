using System.Reflection;
using System.Text.Json;
using Multiplexed.AI.Sdk.Contracts.Boundary;

namespace Multiplexed.AI.Sdk.Contracts.Tests.Boundary
{
    /// <summary>Closes the public contract surface against server-owned identities and CLR-specific payload types.</summary>
    public sealed class AiSdkPublicSurfaceClosureTests
    {
        private static readonly string[] ForbiddenPublicIdentityFragments =
        [
            "SharedRunId",
            "LocalRunId",
            "RuntimeInstanceId",
            "PreferredRuntimeInstanceId",
            "WorkerId",
            "ClaimToken",
            "Lease",
            "Epoch",
            "TenantId",
            "TenantGroupId",
            "ControlPlaneId",
            "Partition"
        ];

        [Fact]
        public void Exported_Contracts_Do_Not_Expose_Server_Owned_Identity_Names()
        {
            var properties = typeof(AiSdkContractAssembly).Assembly
                .GetExportedTypes()
                .SelectMany(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                    .Select(property => $"{type.FullName}.{property.Name}"))
                .ToArray();

            foreach (var property in properties)
            {
                Assert.DoesNotContain(
                    ForbiddenPublicIdentityFragments,
                    fragment => property.Contains(fragment, StringComparison.OrdinalIgnoreCase));
            }
        }

        [Fact]
        public void Exported_Contract_Properties_Use_Only_Portable_Wire_Types()
        {
            var properties = typeof(AiSdkContractAssembly).Assembly
                .GetExportedTypes()
                .SelectMany(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                    .Select(property => (Owner: type, Property: property)))
                .ToArray();

            Assert.NotEmpty(properties);
            foreach (var item in properties)
            {
                Assert.True(
                    IsPortable(item.Property.PropertyType),
                    $"Public contract property '{item.Owner.FullName}.{item.Property.Name}' uses non-portable CLR type '{item.Property.PropertyType}'.");
            }
        }

        [Fact]
        public void Public_Contract_Surface_Does_Not_Expose_Object_ByteArray_Exception_Or_Type()
        {
            var forbidden = new[] { typeof(object), typeof(byte[]), typeof(Exception), typeof(Type) };
            var properties = typeof(AiSdkContractAssembly).Assembly
                .GetExportedTypes()
                .SelectMany(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                .ToArray();

            foreach (var property in properties)
            {
                Assert.DoesNotContain(property.PropertyType, forbidden);
                Assert.DoesNotContain(
                    Flatten(property.PropertyType),
                    type => forbidden.Contains(type));
            }
        }

        private static bool IsPortable(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;

            if (type == typeof(byte[]) || type == typeof(object) || type == typeof(Exception) || type == typeof(Type))
                return false;

            if (type.IsArray)
                return type.GetElementType() is { } element && element != typeof(byte) && IsPortable(element);

            if (type.IsEnum || type.IsPrimitive || type == typeof(string) || type == typeof(decimal) ||
                type == typeof(DateTimeOffset) || type == typeof(JsonElement))
                return true;

            if (type.Namespace?.StartsWith("Multiplexed.AI.Sdk.Contracts", StringComparison.Ordinal) == true)
                return true;

            if (!type.IsGenericType)
                return false;

            var definition = type.GetGenericTypeDefinition();
            if (definition != typeof(IReadOnlyList<>) &&
                definition != typeof(IReadOnlyCollection<>) &&
                definition != typeof(IReadOnlyDictionary<,>))
                return false;

            return type.GetGenericArguments().All(IsPortable);
        }

        private static IEnumerable<Type> Flatten(Type type)
        {
            yield return type;
            if (type.IsArray && type.GetElementType() is { } element)
            {
                foreach (var nested in Flatten(element)) yield return nested;
            }
            if (type.IsGenericType)
            {
                foreach (var argument in type.GetGenericArguments())
                foreach (var nested in Flatten(argument))
                    yield return nested;
            }
        }
    }
}
