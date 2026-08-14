using System.Globalization;
using System.Text.Json;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Identity;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Identity;

namespace Multiplexed.AI.Tests.Unit.Runtime.Execution.Composition.ChildDag.Identity
{
    /// <summary>
    /// Provides unit tests for <see cref="AiChildInvocationKeyFactory"/>.
    /// </summary>
    public sealed class AiChildInvocationKeyFactoryTests
    {
        /// <summary>
        /// Verifies that the same authoritative identity tuple always produces the same key.
        /// </summary>
        [Fact]
        public void Create_Should_Return_Same_Key_For_Same_Identity()
        {
            var identity = CreateIdentity();

            var first = AiChildInvocationKeyFactory.Create(identity);
            var second = AiChildInvocationKeyFactory.Create(identity);

            Assert.Equal(first, second);
        }

        /// <summary>
        /// Verifies the frozen version-one canonical encoding against a known deterministic vector.
        /// </summary>
        [Fact]
        public void Create_Should_Match_Known_Version_One_Vector()
        {
            var key = AiChildInvocationKeyFactory.Create(CreateIdentity());

            Assert.Equal(
                "child-invocation-83cdac9de36f7a3a342eed09b8621615ee0a314d1a4e0e82b6a4113bac194971",
                key);
        }

        /// <summary>
        /// Verifies that serialization and deserialization preserve the typed invocation identity and derived key.
        /// </summary>
        [Fact]
        public void Create_Should_Remain_Stable_After_Identity_Serialization_Roundtrip()
        {
            var identity = CreateIdentity();
            var json = JsonSerializer.Serialize(identity);
            var restored = JsonSerializer.Deserialize<AiChildInvocationIdentity>(json);

            Assert.NotNull(restored);
            Assert.Equal(identity, restored);
            Assert.Equal(
                AiChildInvocationKeyFactory.Create(identity),
                AiChildInvocationKeyFactory.Create(restored));
        }

        /// <summary>
        /// Verifies that each authoritative string component contributes independently to the derived key.
        /// </summary>
        [Theory]
        [InlineData("TenantId")]
        [InlineData("ParentExecutionId")]
        [InlineData("ParentCallSiteId")]
        [InlineData("ChildDagId")]
        [InlineData("ChildDagDefinitionVersion")]
        [InlineData("CanonicalLogicalInvocationKey")]
        public void Create_Should_Change_Key_When_String_Identity_Component_Changes(string component)
        {
            var baseline = CreateIdentity();
            var changed = component switch
            {
                "TenantId" => baseline with { TenantId = "tenant-b" },
                "ParentExecutionId" => baseline with { ParentExecutionId = "parent-execution-002" },
                "ParentCallSiteId" => baseline with { ParentCallSiteId = "portfolio-risk" },
                "ChildDagId" => baseline with { ChildDagId = "risk-analysis" },
                "ChildDagDefinitionVersion" => baseline with { ChildDagDefinitionVersion = "2026-08-14.2" },
                "CanonicalLogicalInvocationKey" => baseline with
                {
                    CanonicalLogicalInvocationKey = "portfolio-42|EURUSD|fundamental-research"
                },
                _ => throw new ArgumentOutOfRangeException(nameof(component), component, null)
            };

            Assert.NotEqual(
                AiChildInvocationKeyFactory.Create(baseline),
                AiChildInvocationKeyFactory.Create(changed));
        }

        /// <summary>
        /// Verifies that a new explicit invocation generation creates a distinct logical child identity.
        /// </summary>
        [Fact]
        public void Create_Should_Change_Key_When_Invocation_Generation_Changes()
        {
            var generationZero = CreateIdentity();
            var generationOne = generationZero with { InvocationGeneration = 1 };

            Assert.NotEqual(
                AiChildInvocationKeyFactory.Create(generationZero),
                AiChildInvocationKeyFactory.Create(generationOne));
        }

        /// <summary>
        /// Verifies that length-prefixed canonical encoding preserves field boundaries.
        /// </summary>
        [Fact]
        public void Create_Should_Not_Collide_When_Concatenated_Text_Would_Be_Ambiguous()
        {
            var first = CreateIdentity() with
            {
                TenantId = "ab",
                ParentExecutionId = "c"
            };

            var second = CreateIdentity() with
            {
                TenantId = "a",
                ParentExecutionId = "bc"
            };

            Assert.NotEqual(
                AiChildInvocationKeyFactory.Create(first),
                AiChildInvocationKeyFactory.Create(second));
        }

        /// <summary>
        /// Verifies that key generation does not depend on the current process culture.
        /// </summary>
        [Fact]
        public void Create_Should_Be_Culture_Independent()
        {
            var identity = CreateIdentity();
            var originalCulture = CultureInfo.CurrentCulture;
            var originalUiCulture = CultureInfo.CurrentUICulture;

            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
                CultureInfo.CurrentUICulture = new CultureInfo("tr-TR");
                var turkish = AiChildInvocationKeyFactory.Create(identity);

                CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
                CultureInfo.CurrentUICulture = new CultureInfo("fr-FR");
                var french = AiChildInvocationKeyFactory.Create(identity);

                Assert.Equal(turkish, french);
            }
            finally
            {
                CultureInfo.CurrentCulture = originalCulture;
                CultureInfo.CurrentUICulture = originalUiCulture;
            }
        }

        /// <summary>
        /// Verifies that generation zero is accepted as the initial durable generation.
        /// </summary>
        [Fact]
        public void Create_Should_Accept_Generation_Zero()
        {
            var exception = Record.Exception(
                () => AiChildInvocationKeyFactory.Create(CreateIdentity()));

            Assert.Null(exception);
        }

        /// <summary>
        /// Verifies that negative invocation generations are rejected before hashing.
        /// </summary>
        [Fact]
        public void Create_Should_Reject_Negative_Invocation_Generation()
        {
            var identity = CreateIdentity() with { InvocationGeneration = -1 };

            Assert.Throws<ArgumentOutOfRangeException>(
                () => AiChildInvocationKeyFactory.Create(identity));
        }

        /// <summary>
        /// Verifies that child DAG composition requires an explicit definition version at the identity boundary.
        /// </summary>
        [Fact]
        public void Create_Should_Reject_Empty_Child_Dag_Definition_Version()
        {
            var identity = CreateIdentity() with { ChildDagDefinitionVersion = string.Empty };

            Assert.Throws<ArgumentException>(
                () => AiChildInvocationKeyFactory.Create(identity));
        }

        /// <summary>
        /// Creates the canonical identity used by deterministic key tests.
        /// </summary>
        /// <returns>A complete generation-zero child invocation identity.</returns>
        private static AiChildInvocationIdentity CreateIdentity()
        {
            return new AiChildInvocationIdentity
            {
                TenantId = "tenant-a",
                ParentExecutionId = "parent-execution-001",
                ParentCallSiteId = "portfolio-analysis",
                ChildDagId = "market-analysis",
                ChildDagDefinitionVersion = "2026-08-14.1",
                CanonicalLogicalInvocationKey = "portfolio-42|MSFT|fundamental-research",
                InvocationGeneration = 0
            };
        }
    }
}
