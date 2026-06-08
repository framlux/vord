// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Auth;

namespace Framlux.FleetManagement.Test.Auth;

/// <summary>
/// Tests the global-by-default antiforgery enrollment decision in <see cref="AntiforgeryEnrollment"/>.
/// Covers the verb classification, the opt-out attribute, the null/empty input boundaries, and the
/// matrix of combinations between verbs and attributes.
/// </summary>
public sealed class AntiforgeryEnrollmentTests
{
    // ============================== ShouldEnforce(verbs, attributes) ==============================

    [Test]
    public async Task ShouldEnforce_AllSafeVerbs_ReturnsFalse()
    {
        bool result = AntiforgeryEnrollment.ShouldEnforce(["GET"], []);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task ShouldEnforce_GetAndHead_ReturnsFalse()
    {
        bool result = AntiforgeryEnrollment.ShouldEnforce(["GET", "HEAD"], []);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task ShouldEnforce_OnlyHead_ReturnsFalse()
    {
        bool result = AntiforgeryEnrollment.ShouldEnforce(["HEAD"], []);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task ShouldEnforce_OnlyOptions_ReturnsFalse()
    {
        bool result = AntiforgeryEnrollment.ShouldEnforce(["OPTIONS"], []);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task ShouldEnforce_VerbsAreCaseInsensitive_ReturnsFalseForLowercaseSafeVerbs()
    {
        bool result = AntiforgeryEnrollment.ShouldEnforce(["get", "head"], []);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task ShouldEnforce_Post_ReturnsTrue()
    {
        bool result = AntiforgeryEnrollment.ShouldEnforce(["POST"], []);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task ShouldEnforce_Put_ReturnsTrue()
    {
        bool result = AntiforgeryEnrollment.ShouldEnforce(["PUT"], []);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task ShouldEnforce_Delete_ReturnsTrue()
    {
        bool result = AntiforgeryEnrollment.ShouldEnforce(["DELETE"], []);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task ShouldEnforce_Patch_ReturnsTrue()
    {
        bool result = AntiforgeryEnrollment.ShouldEnforce(["PATCH"], []);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task ShouldEnforce_MixedSafeAndUnsafeVerbs_ReturnsTrue()
    {
        // An endpoint that accepts both GET and POST is still state-changing for the POST case;
        // enrollment must opt the endpoint in.
        bool result = AntiforgeryEnrollment.ShouldEnforce(["GET", "POST"], []);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task ShouldEnforce_PostWithOptOutAttribute_ReturnsFalse()
    {
        object[] attributes = [new SkipAntiforgeryAttribute()];

        bool result = AntiforgeryEnrollment.ShouldEnforce(["POST"], attributes);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task ShouldEnforce_PostWithOptOutAmongOtherAttributes_ReturnsFalse()
    {
        // Opt-out attribute alongside other unrelated attributes is still respected.
        object[] attributes =
        [
            new ObsoleteAttribute(),
            new SkipAntiforgeryAttribute(),
            new SerializableAttribute(),
        ];

        bool result = AntiforgeryEnrollment.ShouldEnforce(["POST"], attributes);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task ShouldEnforce_PostWithoutOptOutAttribute_ReturnsTrue()
    {
        object[] attributes = [new ObsoleteAttribute()];

        bool result = AntiforgeryEnrollment.ShouldEnforce(["POST"], attributes);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task ShouldEnforce_NullVerbs_ReturnsTrue()
    {
        // A null verbs array is a misconfigured endpoint; the safer default is to enforce.
        bool result = AntiforgeryEnrollment.ShouldEnforce(null, []);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task ShouldEnforce_EmptyVerbs_ReturnsTrue()
    {
        bool result = AntiforgeryEnrollment.ShouldEnforce([], []);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task ShouldEnforce_NullAttributes_PostVerb_ReturnsTrue()
    {
        bool result = AntiforgeryEnrollment.ShouldEnforce(["POST"], null);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task ShouldEnforce_NullAttributes_GetVerb_ReturnsFalse()
    {
        bool result = AntiforgeryEnrollment.ShouldEnforce(["GET"], null);

        await Assert.That(result).IsFalse();
    }

    // ============================== ShouldEnforce(EndpointDefinition) ==============================

    [Test]
    public async Task ShouldEnforce_NullEndpoint_Throws()
    {
        await Assert.That(() => AntiforgeryEnrollment.ShouldEnforce(null!))
            .Throws<ArgumentNullException>();
    }

    // ============================== HasOnlySafeVerbs internals ==============================

    [Test]
    public async Task HasOnlySafeVerbs_Null_ReturnsFalse()
    {
        // Null verbs are NOT "only safe verbs" — an empty/null verb list does not let us conclude
        // the endpoint is read-only.
        bool result = AntiforgeryEnrollment.HasOnlySafeVerbs(null);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task HasOnlySafeVerbs_Empty_ReturnsFalse()
    {
        bool result = AntiforgeryEnrollment.HasOnlySafeVerbs([]);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task HasOnlySafeVerbs_AllSafe_ReturnsTrue()
    {
        bool result = AntiforgeryEnrollment.HasOnlySafeVerbs(["GET", "HEAD", "OPTIONS"]);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task HasOnlySafeVerbs_SingleUnsafe_ReturnsFalse()
    {
        bool result = AntiforgeryEnrollment.HasOnlySafeVerbs(["GET", "POST"]);

        await Assert.That(result).IsFalse();
    }

    // ============================== HasOptOutAttribute internals ==============================

    [Test]
    public async Task HasOptOutAttribute_Null_ReturnsFalse()
    {
        bool result = AntiforgeryEnrollment.HasOptOutAttribute(null);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task HasOptOutAttribute_Empty_ReturnsFalse()
    {
        bool result = AntiforgeryEnrollment.HasOptOutAttribute([]);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task HasOptOutAttribute_Present_ReturnsTrue()
    {
        object[] attributes = [new SkipAntiforgeryAttribute()];

        bool result = AntiforgeryEnrollment.HasOptOutAttribute(attributes);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task HasOptOutAttribute_OnlyUnrelatedAttributes_ReturnsFalse()
    {
        object[] attributes = [new ObsoleteAttribute(), new SerializableAttribute()];

        bool result = AntiforgeryEnrollment.HasOptOutAttribute(attributes);

        await Assert.That(result).IsFalse();
    }
}
