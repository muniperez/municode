// SPDX-License-Identifier: MPL-2.0

using Municode.Cli.Contracts;

namespace Municode.Cli.Api;

public sealed class ApiException(
    int statusCode, string code, string message, IReadOnlyList<ErrorDetail>? details = null)
    : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public IReadOnlyList<ErrorDetail> Details { get; } = details ?? [];
}
