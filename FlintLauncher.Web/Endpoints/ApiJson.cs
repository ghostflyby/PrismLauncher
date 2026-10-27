// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Small JSON helpers shared by the endpoints (AOT-safe: explicit nodes).

using FlintLauncher.Web.Models;

namespace FlintLauncher.Web.Endpoints;

/// <summary>Uniform {"error":{"code","message"}} bodies.</summary>
public static class ApiJson
{
    public static IResult Error(string code, string message, int statusCode) =>
        Results.Json(new ApiErrorEnvelope(new ErrorResponse(code, message)),
            FlintJsonContext.Default.ApiErrorEnvelope, statusCode: statusCode);

    public static IResult NotFound(string code, string message) => Error(code, message, 404);

    public static IResult Conflict(string code, string message) => Error(code, message, 409);
}
