// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Services.Infrastructure;

namespace Framlux.FleetManagement.Test.Services.Infrastructure;

/// <summary>
/// Tests for <see cref="ServiceResult{T}"/>.
/// Verifies factory methods produce correct status codes and that computed
/// properties correctly classify those codes.
/// </summary>
public sealed class ServiceResultTests
{
    // ========== Ok ==========

    /// <summary>
    /// Verifies that Ok produces a 200 result with data and no error message.
    /// </summary>
    [Test]
    public async Task Ok_SetsStatusCodeAndData()
    {
        ServiceResult<string> result = ServiceResult<string>.Ok("payload");

        await Assert.That(result.StatusCode).IsEqualTo(200);
        await Assert.That(result.Data).IsEqualTo("payload");
        await Assert.That(result.ErrorMessage).IsNull();
    }

    /// <summary>
    /// Verifies that a 200 result is classified as success and not as not-found.
    /// </summary>
    [Test]
    public async Task Ok_IsSuccess_TrueAndIsNotFound_False()
    {
        ServiceResult<string> result = ServiceResult<string>.Ok("data");

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.IsNotFound).IsFalse();
    }

    // ========== NotFound ==========

    /// <summary>
    /// Verifies that NotFound produces a 404 result with null data and no error message.
    /// </summary>
    [Test]
    public async Task NotFound_SetsStatusCode404AndNullData()
    {
        ServiceResult<string> result = ServiceResult<string>.NotFound();

        await Assert.That(result.StatusCode).IsEqualTo(404);
        await Assert.That(result.Data).IsNull();
        await Assert.That(result.ErrorMessage).IsNull();
    }

    /// <summary>
    /// Verifies that a 404 result is flagged as not-found and not as success.
    /// </summary>
    [Test]
    public async Task NotFound_IsNotFound_TrueAndIsSuccess_False()
    {
        ServiceResult<string> result = ServiceResult<string>.NotFound();

        await Assert.That(result.IsNotFound).IsTrue();
        await Assert.That(result.IsSuccess).IsFalse();
    }

    // ========== Error ==========

    /// <summary>
    /// Verifies that Error stores the given status code and data.
    /// </summary>
    [Test]
    public async Task Error_SetsStatusCodeAndData()
    {
        ServiceResult<string> result = ServiceResult<string>.Error(500, "internal error payload");

        await Assert.That(result.StatusCode).IsEqualTo(500);
        await Assert.That(result.Data).IsEqualTo("internal error payload");
    }

    /// <summary>
    /// Verifies that a 500 error result is neither success nor not-found.
    /// </summary>
    [Test]
    public async Task Error_500_IsNotSuccessAndIsNotFound_False()
    {
        ServiceResult<string> result = ServiceResult<string>.Error(500, "error");

        await Assert.That(result.IsSuccess).IsFalse();
        await Assert.That(result.IsNotFound).IsFalse();
    }

    /// <summary>
    /// Verifies that a 299 status code is classified as success (within 2xx range).
    /// </summary>
    [Test]
    public async Task Error_299StatusCode_IsSuccess()
    {
        ServiceResult<string> result = ServiceResult<string>.Error(299, "edge case");

        await Assert.That(result.IsSuccess).IsTrue();
    }

    /// <summary>
    /// Verifies that a 300 status code is not classified as success (outside 2xx range).
    /// </summary>
    [Test]
    public async Task Error_300StatusCode_IsNotSuccess()
    {
        ServiceResult<string> result = ServiceResult<string>.Error(300, "redirect");

        await Assert.That(result.IsSuccess).IsFalse();
    }

    /// <summary>
    /// Verifies that a 199 status code is not classified as success (below 2xx range).
    /// </summary>
    [Test]
    public async Task Error_199StatusCode_IsNotSuccess()
    {
        ServiceResult<string> result = ServiceResult<string>.Error(199, "below range");

        await Assert.That(result.IsSuccess).IsFalse();
    }

    // ========== BadRequest ==========

    /// <summary>
    /// Verifies that BadRequest produces a 400 result with the given error message.
    /// </summary>
    [Test]
    public async Task BadRequest_SetsStatusCode400AndErrorMessage()
    {
        ServiceResult<string> result = ServiceResult<string>.BadRequest("field is required");

        await Assert.That(result.StatusCode).IsEqualTo(400);
        await Assert.That(result.Data).IsNull();
        await Assert.That(result.ErrorMessage).IsEqualTo("field is required");
    }

    /// <summary>
    /// Verifies that a 400 result is not success and not not-found.
    /// </summary>
    [Test]
    public async Task BadRequest_IsNotSuccessAndIsNotFound_False()
    {
        ServiceResult<string> result = ServiceResult<string>.BadRequest("invalid");

        await Assert.That(result.IsSuccess).IsFalse();
        await Assert.That(result.IsNotFound).IsFalse();
    }

    // ========== Forbidden ==========

    /// <summary>
    /// Verifies that Forbidden produces a 403 result with the given error message.
    /// </summary>
    [Test]
    public async Task Forbidden_SetsStatusCode403AndErrorMessage()
    {
        ServiceResult<string> result = ServiceResult<string>.Forbidden("access denied");

        await Assert.That(result.StatusCode).IsEqualTo(403);
        await Assert.That(result.Data).IsNull();
        await Assert.That(result.ErrorMessage).IsEqualTo("access denied");
    }

    /// <summary>
    /// Verifies that a 403 result is not success and not not-found.
    /// </summary>
    [Test]
    public async Task Forbidden_IsNotSuccessAndIsNotFound_False()
    {
        ServiceResult<string> result = ServiceResult<string>.Forbidden("denied");

        await Assert.That(result.IsSuccess).IsFalse();
        await Assert.That(result.IsNotFound).IsFalse();
    }

    // ========== Conflict ==========

    /// <summary>
    /// Verifies that Conflict produces a 409 result with the given error message.
    /// </summary>
    [Test]
    public async Task Conflict_SetsStatusCode409AndErrorMessage()
    {
        ServiceResult<string> result = ServiceResult<string>.Conflict("already exists");

        await Assert.That(result.StatusCode).IsEqualTo(409);
        await Assert.That(result.Data).IsNull();
        await Assert.That(result.ErrorMessage).IsEqualTo("already exists");
    }

    /// <summary>
    /// Verifies that a 409 result is not success and not not-found.
    /// </summary>
    [Test]
    public async Task Conflict_IsNotSuccessAndIsNotFound_False()
    {
        ServiceResult<string> result = ServiceResult<string>.Conflict("conflict");

        await Assert.That(result.IsSuccess).IsFalse();
        await Assert.That(result.IsNotFound).IsFalse();
    }

    // ========== IsNotFound boundary ==========

    /// <summary>
    /// Verifies that IsNotFound is false for a 403 status code (not exactly 404).
    /// </summary>
    [Test]
    public async Task IsNotFound_403StatusCode_ReturnsFalse()
    {
        ServiceResult<string> result = ServiceResult<string>.Forbidden("no access");

        await Assert.That(result.IsNotFound).IsFalse();
    }

    /// <summary>
    /// Verifies that IsNotFound is false for a 405 status code (not exactly 404).
    /// </summary>
    [Test]
    public async Task IsNotFound_405StatusCode_ReturnsFalse()
    {
        ServiceResult<string> result = ServiceResult<string>.Error(405, "method not allowed");

        await Assert.That(result.IsNotFound).IsFalse();
    }

    // ========== Generic type parameter ==========

    /// <summary>
    /// Verifies that ServiceResult works with integer data type.
    /// </summary>
    [Test]
    public async Task Ok_WithIntData_StoresValue()
    {
        ServiceResult<int> result = ServiceResult<int>.Ok(42);

        await Assert.That(result.StatusCode).IsEqualTo(200);
        await Assert.That(result.Data).IsEqualTo(42);
        await Assert.That(result.IsSuccess).IsTrue();
    }

    /// <summary>
    /// Verifies that ServiceResult works correctly with a nullable value type as data.
    /// </summary>
    [Test]
    public async Task NotFound_WithNullableIntType_DataIsDefaultNull()
    {
        ServiceResult<int?> result = ServiceResult<int?>.NotFound();

        await Assert.That(result.Data).IsNull();
        await Assert.That(result.IsNotFound).IsTrue();
    }
}
